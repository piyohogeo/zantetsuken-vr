# VR斬鉄剣ゲーム 技術設計書

*即時シェーダ切断と非同期メッシュ／物理更新による、低遅延・反復切断パイプライン*

| 項目 | 内容 |
| --- | --- |
| 文書目的 | Codexで継続更新するプロジェクト設計上の正本 |
| ステータス | Draft v1.5 / PoC実装準備・固定Capture Profile／同期映像／未来評価設計段階 |
| 作成日 | 2026-08-21 |
| 最終更新 | 2026-09-10 |
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

- 表示と物理の不一致時間を短くし、周辺破片が透明な旧Colliderへ接触する状態を最小化する。プレイヤー身体・手は初期仕様ではプロップ／破片とPhysX接触せず、刀も物理衝突させず切断可能時の論理Sweepだけを使用する。人工移動によるモデル化済みOccupancyへの代表的な新規侵入だけを簡易Queryで抑制し、実空間HMDはClampしない。Camera被り、未登録物体の内部視点、即時StencilのCamera-inside破綻を許容し、視界保護はbest-effortとする。

- 生涯切断数や全Pending Cut数ではなく、実際にBatchへ投入する`TemporaryRenderCapRecordSet`、対象の共用Geometry、固定長`SelectedTemporaryClipPlaneSet`が一時描画コストを決める。Geometry Commit済みの境界は一時描画費用へ含めず、Hybrid Clip容量を超えた境界はRenderer費用を増やさない。

- 切断対象の表示とStencil Volumeは、幾何切断に必要なTopologyを持つ同じ共用Geometryを正本とし、一度の切断・Cap生成結果を両用途へ使用する。通常切断は正負二集合とし、各側最大一つの物理所有者へまとめる。Compound Physics Proxyは独立した物理表現として残し、製品用Strict Solid Cut Meshは生成・常駐・Fallbackのいずれにも使用せず、Global Solid Reconstructionは将来研究だけに隔離する。

- 建物由来の動的分裂子には7.2.2の独立World D6を使うが、倒壊防止や変位上限を保証しない。一般外部Jointを製品切断対象から外し、正負二集合・点Anchor・Graphなしの方針を維持する。

- バックグラウンド結果は世代番号で検証し、古い結果を安全に破棄できるようにする。

- 短期プロトタイプでは対象範囲と品質契約を限定し、計測結果に基づいて拡張する。

- 飛翔する斬撃波の到達時間を計算猶予として利用し、命中前に未来姿勢、表示／Stencil共用VP Geometry、Convexの切断を投機的に評価する。

- 予測結果は確定・条件付き・投機の信頼度と世代番号を持ち、実接触時の検証に成功したものだけをコミットする。

- 非同期処理と状態遷移は最初のPoCから相関ID付きで記録し、性能計測と因果関係の調査を同じ時間軸で行えるようにする。

- デバッグ映像はTraceを補助する証拠としてFrameIdと同期し、PoC初期はUnity側の選択的キャプチャ、必要性確認後はOpenXR Projection Swapchain Captureを段階導入する。

## 3. スコープ

### 3.1 初期垂直スライス

- 街区1つ、切断可能プロップ約10種、NPC 1体、刀1本。建物を切断対象に含め、道路は切断対象に含めない。

- 単一切断、連続切断、処理中の再切断を検証する。処理中の再切断は先行`LogicalCutOperation`が公開済みで、Geometry Commit／cook／Physicsだけが未完了の区間を保証対象とする。先行Operation未公開の親とその仮表示領域への後続切断は受付けない。

- 即時clip表示、仮断面、後追い共用VP Geometry切断、Convex差し替えまでを一連で実装。

- 切断対象は閉じた静的メッシュと、切断時に姿勢を固定できるHumanoidに限定。

- 非空の切断結果は寸法だけで消去せず通常Fragmentとして扱い、専用Convexを持たない共用Geometryは既存の物理所有者へ所属させる。

- PCVRを対象とし、実アプリの両眼描画90fpsを性能目標とする。XRコンポジタの再投影は瞬間的な取りこぼしへの安全網であり、常用前提にしない。

- 実装と性能計測は非VRモードから開始し、切断PoC成立後にQuest 3Sの有線Quest Linkで早期XRスモークテストを行う。本格的なVR操作・空間UIは、その後に導入する。

- 剣を素早く振ると三日月形の斬撃波が扇状に広がり、有限速度で飛翔する。接触時の即時分離を主要攻撃表現の目標とし、必要な準備と同フレーム表示の扱いは4.5.2に従う。

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

> **状態モデル** 各切断対象はStable Geometry、必要なGeometry／Physics工程が未完了の受付済みPending Cuts、全切断履歴を保持するCutBoundaryRecord群を持つ。バックグラウンド成果物が`Ready`になっただけではTemporary Rendererを外さず、4.5.6の直接出力・必要転送と公開条件を満たした後、描画フレーム境界でVP Geometry参照公開と`GeometryState = Committed`がともに成功した後にだけ対応するTemporary Clip／Cap描画Recordを`TemporaryRenderCapRecordSet`から除去する。Pending Cut自体は必要工程が正常完了または既存規則で終端するまで維持する。CutBoundaryRecord、Cut Plane、論理FragmentはStable側へ移して保持し、点Anchorは各Cellの継承済み集合を保持する。Collider未完成は別の物理状態軸で管理する。

### 4.1 コンポーネント境界

| サブシステム | 責務 |
| --- | --- |
| Blade Pose Adapter | OpenXR Grip Poseへ持ち手別のGripToKatanaOffsetを適用し、BladeAxis、EdgeDirection、SideNormal、追跡有効性を提供 |
| Blade Sweep Detector | 刀身の連続姿勢からswept volumeとGesture Sampleを構築し、速度・移動量・Edge Direction Gateを評価。対象への最終命中は確定しない |
| Cut State | Stable世代、必要なGeometry／Physics工程が未完了の受付済みPending Cut列、Temporary Render Boundary列、永続CutBoundaryRecord／論理破片、ジョブ状態、上限管理 |
| Temporary Slice Renderer | clip、論理破片の分離オフセット、仮断面、切断縁演出 |
| Visual Slice Worker | 4.5.2で導入を採用した未来Rig PoseのJobベイク・VP入力準備、VP入力の三角形切断、断面生成、属性補間、VPプールへの正負Index直接出力 |
| Physics Slice Worker | Convex平面クリップ、質量特性計算、Collider Bake／cooking |
| Commit Controller | 世代検証後、描画フレーム／物理ステップ境界で安全に差し替え。7.9の任意分割・物理GCも既存担当の公開・退役境界へ接続する。Phase 4.3で建物World D6の生成・退役を同じ境界へ接続する |
| Slash Gesture／Wave Simulator | 刀軌道から切断面と初期SlashFrontを早期Latchし、SpanAxisに対して一価・単調な粗い折れ線前縁の飛翔、Extending中の頂点／辺追加、逆行・自己交差によるFinalized、到達予定時刻、実接触を管理。VFXの前縁はこの判定形状と一致させる |
| Future Evaluation Scheduler | 初期版は固定優先度Class、締切、固定容量、Schedule前取消だけを扱う差し替え可能なDispatch境界とし、将来版で実測費用・信頼度・aging等を追加する。実行済みJob成果物は世代検証で破棄・再利用する |
| Prediction Physics | 必要な局所物理島を独立PhysicsSceneで先読みし、命中予定姿勢を生成 |
| Mob Future Planner | 副作用のない固定ステップ移動Kernelと`AnimationPlannerV1`からMobPlanのRoot軌道と`ExplicitAnimationStateV1`を生成し、Nearのライブ更新、Mid／Farの軌道再生、粗い無効化を同じ世代契約で接続 |
| Animation Pose Evaluator | immutableな明示Animation Stateと対象`FixedStepId`からcanonical Bone順のRig Poseを生成する。controllerなしPlayable／Mixer、Pose Table等を交換可能Backendとし、AnimatorController内部状態をCurrent／Futureの正本にしない |
| Observability／Trace | Profiler計測、状態イベント、Work Item／Job相関、固定長履歴、異常時保存、Editorタイムラインを提供 |
| Visual Capture | Unity側の選択的片眼録画と異常時静止画をTraceへ関連付け、後期にはOpenXR API LayerによるProjection Swapchain Captureを提供 |
| Asset Preprocessor | Blenderをヘッドレス実行し、ライセンスAssetから表示／Stencil共用Geometry、幾何Topology、点Anchor、Compound Physics Proxy、検証レポートをローカル生成。Phase 5.5で建物由来Metadataと初期Depthを生成する。製品用Strict Solidは生成しない |

### 4.2 切断イベントの時系列

- 刀の連続姿勢を収集し、Edge Direction Gateを通過したGestureだけをSlash候補とする。この段階では対象の命中も対象世代も変更しない。

- 十分な軌道が得られた時点で`SlashId`、`SlashGeneration`、切断面、粗い折れ線の初期`SlashFront`をLatchする。三日月VFXと前縁の飛翔・命中判定を同時に開始し、初期前縁と重なる対象はその時点で実命中とする。

- 切断面、最大飛距離、許容する最大前縁範囲から保守的な`Candidate Flight Bounds`を作り、候補対象を列挙する。各対象の`BaseObjectGeneration`を記録して未来姿勢、表示／Stencil共用VP Geometry、Convex切断を投機開始するが、候補列挙だけではPending Cutを追加しない。

- Extending中も既存`SlashFront`を停止させず有限速度で前進させ、観測された振りのうち`SpanAxis`方向へ単調に進む部分だけを同一平面内の頂点／辺として追加する。微小な逆行は手ぶれとして無視し、明確な逆行、非隣接辺との交差、頂点順序の反転では現在SlashをFinalizedする。各辺を前フレーム位置から現在位置まで細い帯状にSweepし、三日月VFXの前縁と同じ形状で実交差を確認する。

- 実命中時に`HitConfirmed`を記録し、対象状態を変更せず更新前の基底世代、実姿勢、採用切断面と物理入力Snapshotを確定する。初期対象の剛体は19.5.1でLocal Planeを採用／Fallback確定し、その他の対象は従来の実命中Poseから面を得る。Mesh／Colliderの完成待ちや投機成果物のReady状態を面選択条件にしない。

- 命中した確定済み親LogicalFragmentが、`LogicalCutOperation`未公開のPending Cutによって置換予定である場合、その親とPending Cutの面から得る仮表示領域への後続切断を受付けない。対象状態、`ObjectGeneration`、先行Pending Cutとその仕事を変更せず、後続用のCutOperationId、Pending Cut、仕事を作らず、要求を保存・再実行しない。この制限は当該親と仮表示領域だけに適用し、無関係な確定済み対象へ広げない。先行Operationが公開された時点で解除し、Geometry Commit／cook／Physicsの未完了だけを理由に継続しない。

- Pending Cutの受付、対象世代更新、即時表示、Provisional生成より前に、7.6の現在採用Convex集合を同じSnapshot・面・epsilonで分類する。一方の配分候補が0件なら正常な片側空No-opとして、未完了切断数が既存容量設定の`MaxIncompleteCutOperationCount`以上なら混雑見送りとして終了する。どちらも対象状態、`ObjectGeneration`、支持、表示、物理、既存履歴を変更せず、LogicalCutOperation、論理子、CutBoundaryRecord、Pending Cut、切断／cook／Commit仕事を作らない。No-op用の投機成果物は公開せず既存の不採用・回収経路へ流し、見送った要求を保存・再実行しない。Hitの観測と既存の非破壊通知は残せる。

- 上記の受付を通過した場合だけ、正の`CutOperationId`、親LogicalFragment、更新前の基底世代、採用面、対象の`ObjectGeneration`更新、Pending Cut追加を原子的に公開する。この時点では論理子、CutBoundaryRecord、LogicalCutOperationを公開しない。受付済みPending Cutの即時clip／Stencil／仮Capは採用面を使い、4.5.2の必要なベイク・VP変換後に開始する。物理固定を描画省略条件にせず、固定側のOffset／Impulseは0とする。点Anchor配分が未完了なら物理適用は既存Work依存で待ち、支持待ち専用状態を作らない。

- 受付後の通常処理で正負各最大1の非空の直接子と実在する切断面参照が確定し、5.6の構築条件を通過した時点で、Fragment、CutBoundaryRecord、LogicalCutOperationを原子的に公開する。Pending Cut由来の即時表示を同じ採用面のOperation由来表示へ重複なく引き継ぎ、対応GeometryのCommitまで継続する。

- FixedSupportAnchorの子Cellへの所属は、7.1の入力CellのAnchor集合、採用面、local位置とframe写像から決め、確定子と同じ境界で公開する。No-op、受付見送りまたはOperation公開前の終端失敗では公開しない。各物理所有者は配分されたAnchorが一つでもあれば固定、なければ動的とする。受付時に暫定子、Side PathまたはAnchor専用状態を作らない。

- `LogicalCutOperation`未公開のまま、既存規則が受付済み切断を終端失敗と確定した場合だけ、その切断に由来するPending Cutと仮表示を退役させる。親の現在有効な共用Geometry、先行する有効なPending制約と履歴、既存物理および現在の姿勢・速度を維持し、`ObjectGeneration`と使用済みIDを巻き戻さない。単なる未完了、実行待ち、一時的な容量不足には適用せず、公開済みOperationまたはProvisional物理をrollbackしない。実行中Jobの完了と成果物回収を継続し、対応する有効なPending CutとCutOperationIdが存在しない後着成果物は世代が一致しても公開しない。安全な終端・回収後に未完了件数を一度だけ減らし、元の親を最新世代で新規切断の受付対象へ戻す。失敗した切断と、その間に見送った要求は再実行しない。

- 投機成果物が命中したSlash／Segment、確定した`SlashFrame`、基底対象世代と一致し、対応する有効なPending Cutと`CutOperationId`が現在も一致し、通常の予測姿勢検証または19.5.1の剛体Local Plane採用検証を通れば、確定した操作Local Planeに一致する表示・物理成果物を描画フレーム／物理ステップ境界でコミットする。Actorを予測World姿勢へ移動しない。

- Pending Cutの受付情報は一時描画集合とは別に、当該切断に必要なGeometry／Physics工程が正常完了または既存規則で終端するまで維持する。Geometry CommitによりPending Clip／Capを`TemporaryRenderCapRecordSet`から回収しても、未完了の有効な物理工程とそのCommit照合を失効させない。公開済み`LogicalCutOperation`が履歴に存在することだけでは受付または工程の有効性を証明しない。

- 投機成果物が未完成なら、19.5.1で採用したLocal Planeは維持し、その面の既存Jobを継続／優先化する。投機Local Planeが不採用または存在しない場合だけ、実命中のSourceSlashPlaneを実姿勢へ変換したLocal Planeで表示／Stencil共用VP GeometryとConvex切断を優先ジョブへ投入する。受付済み切断が進行可能な間は、採用後の個別成果物失敗でも操作Local Planeを変えず同じ面の既存処理を継続する。`LogicalCutOperation`未公開の終端失敗では上記の限定退役に従う。Operation公開前後で同じ採用面の仮表示を引き継ぎ、Geometry Commitまで継続する。

- Readyは有効な表示切断CPU出力の完成を表す。4.5.6の必要転送と現在の参照・frame・世代条件を満たした後、描画境界でVP Geometry参照公開とGeometryState＝Committedを原子的に行う。成功後だけ対応Temporary描画を回収し、CutBoundaryRecord、Cut Plane、論理Fragmentと再切断に必要な履歴は保持する。Collider未完成はPendingPhysicsSplit／PendingAnchoredSplit等で別に追跡する。

### 4.3 バックグラウンド実行モデル

フレーム内または複数フレームにまたがるCPU計算は、C# `Task`を大量発行せず、Unity C# Job SystemとBurstを基本とする。メインスレッドはUnity Objectを数値スナップショットへ変換し、締切と優先度に従ってJobをBatch Scheduleする。Job本体は`NativeArray`、`NativeList`、`NativeStream`等のアンマネージデータだけを扱い、GameObject、Component、Transform、Renderer、Rigidbodyを直接操作しない。

- Job向き：候補交差、三角形分類、表示／Stencil共用VP Geometry切断、Convex平面クリップ、断面・質量特性生成、未来軌道／MobPlanのBatch評価、未来Rig Poseからの頂点スキニング・VP形式への変換（4.5.2）、対応APIによるCollider Bake、7.9の任意分割時のIndex振り分けコピー。

- メインスレッド向き：通常命中の同期BakeMesh・CPUデータ取得／VP変換、Backendに応じたPose評価（19.3）、JobのSchedule・完了回収、`JobHandle`依存関係、世代／命中検証、表示VPの範囲管理・GPU更新・Geometry参照公開、物理用`MeshData`のMesh適用、Rigidbody／Collider生成、描画フレーム／物理ステップ境界のCommit。

- `Task`／Unity `Awaitable`向き：ファイルI/O、Trace／録画保存、Editorツール、外部プロセス待機、Unity非同期APIの進行制御。CPU幾何計算の標準実行基盤にはしない。

極小Jobを対象ごとに無制限発行せず、同種処理を`IJobFor`／`IJobParallelFor`等でBatch化する。JobはSchedule後に中断できないため、投機前提が崩れた場合もメインスレッドから`Complete`を強制せず、完了後にGeneration不一致として破棄する。`TaskId`はC# `Task`型を意味せず、Job、I/O、GPU処理を含む論理Work Itemの相関IDとして維持する。

7.9の任意分割もこのJob／Main Thread境界を使い、全体1件の試行を探索から必要cook・採否・回収まで保持する。物理GCの登録終了は既存の安全なMain Thread／物理Step境界へ接続し、専用WorkerやC# Task大量発行を追加しない。

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
| 0 | `CriticalPhysicsSafety` | 点Anchor配分、Impulse／Offset可否、物理Commit安全条件 |
| 1 | `ConfirmedPhysics` | 命中済みConvex切断、Fast Cook、Collider分裂に必要な処理 |
| 2 | `ConfirmedGeometry` | 命中済み共用Geometry、実Cap、Stable Geometryへの置換 |
| 3 | `NearDeadlinePrediction` | 命中前だが到達締切が近いMesh／Convex／姿勢の投機評価 |
| 4 | `BackgroundMaintenance` | Fast Simulation再cook、遠距離MobPlan延長、未来Animation焼き込み、Cache／品質向上、7.9の任意分割・物理GC |

同一Class内は`HasDeadline=true`を先にして`DeadlineTimestamp -> EnqueueSequence`のstable順とし、Deadlineなしは同Class末尾へ置く。比較は`HasDeadline`を別keyとして行い、内部に正の無限大を生成しない。V1は命中確率、信頼度、画面面積、厳密な費用式、aging、動的Class変更を順位計算へ入れず、`EstimatedCostBucket`は計測とDispatch予算の粗い控除にだけ使う。低優先度のstarvationは許容可能な品質低下としてCounterへ記録し、物理安全を逆転させる公平化は行わない。

QueueとWork Tokenは起動時に固定長領域を確保し、実行中の成長とGC allocationを禁止する。V1は単一の固定長binary heapまたは同等の固定Class列でよく、内部表現をAPIへ公開しない。`TotalQueueCapacity`に加えて`CriticalReservedSlots`を持ち、Class 2～4は予約分を消費できず、Class 0～1だけが全容量を利用できる。満杯時に既存Itemを追い出したりDispatcher内で待機せず`CapacityExceeded`を返す。呼出側は既存の即時Renderer、旧Collider共有、未延長MobPlan等の各機能固有Fallbackを継続し、同一Frame内で無制限再試行しない。

切断受付後の必須仕事が`CapacityExceeded`となった場合だけ、切断Coordinatorは既存のSchedule、Job完了、結果回収を通常フレーム予算を超えて同期的に進め、必要な空きを作ってよい。空きを作る処理自身の単純待機、進捗のない再投入、PlayerLoop／Commitの再入、全切断の一括完了待ちを行わない。次の物理Step等が必要なら既存の持ち越し経路へ戻す。この例外は切断側だけに限定し、Dispatcher API、Capture／Trace、描画、Geometry失敗処理をblockingへ変更しない。

`DispatchBudget`は1 Tickの`MaxScheduleCount`、`MaxEstimatedWorkerCost`、Job種別ごとの既存同時実行上限を持つ。選択した同種Itemは可能な範囲でBatch化するが、高優先Itemを低優先Batchの完成待ちへ依存させない。BackgroundはClass 0～3のReady Itemがなく、予約済みWorker／Bake枠と当該Tick予算に余裕がある場合だけScheduleする。Fast CookはClass 1、後追いFast Simulation再cookはClass 4とする。共用Geometryは即時Rendererで隠せるためClass 2とし、物理安全処理より先にしない。

V1の異常処理は、無効Descriptorの受付拒否、容量超過、Schedule前取消、Schedule済み成果物のGeneration Reject、二重Completion拒否だけを必須とする。優先度継承、deadline miss recovery、queue間work stealing、adaptive cost learning、aging、複数端末別係数、厳密なCPU予約は実装しない。これらはProfiler CounterとT-016／T-076の実測後、必要なものだけを後Phaseの`FutureEvaluationDispatcherV2`へ追加する。V1実装の内部を破棄しても、上記API、PriorityClassの意味、Work Token、Trace相関、世代Commit契約は維持する。

観測は既存`TaskScheduled／TaskStarted／TaskCompleted／TaskCancelled／CommitRejected／ResultDisposed`を使用する。V1 Dispatcherを通るTask lifecycle Eventでは共通`TaskId`をWork Tokenへ一致させ、`Value0=PriorityClass`、`Value1=EnqueueSequence`とし、いずれもuint値をbinary64へ正確に格納する。Deadlineと費用BucketはProfiler側へ記録し、既存Trace schemaへ追加fieldを設けない。受付失敗はV1では個別Trace Eventを増やさずOutcomeとCounterへ反映する。Profiler CounterはClass別Queued数、Running数、Schedule数、CapacityExceeded数、SequenceExhausted数、Deadline超過数、最古待機時間、Critical予約枠残数、Tickごとの推定／実Worker時間を最低限とする。切断受付側は片側空No-opと混雑見送りを`CutNoOpCount`／`CutAdmissionSkippedCount`で区別し、専用Trace Eventや公開enumを追加しない。V1はDeadline超過を自動修復せず、機能固有Fallbackを継続して計測事実だけを残す。

命中済みのGeometry出力はClass 2のConfirmedGeometryとする。物理に必要なCPU MetadataはGPU転送前に利用可能にし、物理が表示仕上げを待つ循環を作らない。実データ依存はDAGで解決し、優先度だけで物理の先着を保証せず、preemption・優先度継承・別Schedulerを追加しない。導入採用後の未来用Jobベイクは入力準備から既存DAG・Dispatch予算へ含め、命中Deadlineに応じたClass 3／4で投入・完了回収する。候補全件を同期ベイクしてから切断Jobだけを投入しない。Schedule済みJobの非preemptiveな共有枠占有は既存規則に従う。

### 4.5 実行時表示Geometryと描画段階

Windows PCVR／Unity 6.3 LTS／URPで、メインスレッドの直列処理と同期待ちを減らすため、通常Mesh表示からVertex Pulling（VP）へ移行する。draw call数の最小化自体を目的にせず、照明はグローバルな並行光源1つ＋ambientとする。表現・変換・メモリ管理・描画発行をPhase 0.9～0.94で先に成立させ、切断・Cap・物理・未来予測は後続Phaseで接続する。

#### 4.5.1 表現・正本・Component参照

アセット読み込み時の表示表現はUnity Meshとし、同形状のInstanceはMeshを共有する。即切断と実切断後の表示／StencilはVPを使い、通常命中の同期ベイク・Mesh→VP変換と、未来予測用JobからのVP入力生成は4.5.2に従う。有効な変換済み入力は再利用する。切断生成破片はUnity Meshへ戻さず、スキニング中の通常描画はSkinnedMeshRendererに残す。一時ベイクMeshと元Mesh由来のskinning入力は独立した切断正本ではない。

VPはグローバルVertex／Index Buffer方式、VertexはAoSとする。CPU側VP表現を切断Geometryの正本、GPU側を描画用の実質的コピーとし、Geometry参照とInstanceのTransform／frame写像等を分離する。同じ現在GeometryについてUnity MeshまたはCPU側VPとは別に、並行して更新・切断する権威メッシュを持たない。表示とStencilは一度の切断結果から同じ面集合・属性・Topologyを使う。GPUコピー、Topology Metadata、切断作業構造・Cache、非同期用旧世代、不変の投機Snapshot、未採用VP入力・出力は許容する。元共有Meshも他の未切断Instanceが使う限り保持でき、Physics Convexは独立表現のままとする。

VPのGeometry参照はグローバルIndex範囲等で表し、複数参照による選択描画と、同じGeometryを別Transformで描くInstanceを扱う。ClosedCutComponentSet／ComponentFragment等のMetadataは幾何切断・既存Asset表現に必要な情報だけとし、全島の列挙や島別の恒久ID・物理所有者・描画範囲構築を要求しない。通常切断の出力配置は4.5.6、追加空間分割での範囲内部の面配分は7.9に従う。

Geometry参照、正負集合を表すLogicalFragment、物理所有単位、描画要求を同一視しない。別Topologyであることだけで別LogicalFragment／Rigidbody／drawを要求せず、通常の物理出力と所属は7.2.1／7.6に従う。

AoSの属性集合・strideは各実装時点で固定し、属性追加時に変換・切断属性処理・shaderを更新できる境界を残す。永久固定のlayout、実行中schema変更、複数layout共存、旧Bufferの無停止移行を要求しない。

#### 4.5.2 準備と表示採用

通常命中で即切断開始に必要なSkinned入力は、現在の実Bone Poseを使う同期`SkinnedMeshRenderer.BakeMesh(mesh, useScale: false)`→CPUデータ取得・VP変換を基本経路とする。未来予測用に限り、Phase 4.65で`ResolvedAnimationPoseInput`→19.3の不変Rig Pose→Burst Jobによる線形ブレンドスキニング→共通CPU側VP入力を限定実装し、同期経路との比較から導入の採否を決める。目的は複数候補の頂点処理と同期待ちをMain Threadへ集中させないことであり、ベイク総時間の短縮や負荷ゼロを要求しない。非スキニング対象はベイクを省き、必要な形状・姿勢で準備済みなら再利用する。

Skinned対象の切断前の共通VP入力は、元SkinnedMeshRendererのTransformを基準とするlocal空間とし、Root Bone localやWorld空間を混在させない。同期経路と比較基準のBakeMeshはともに`useScale: false`へ固定する。Job経路はRoot Boneを含む骨Poseと対応するbindposeをRenderer基準へ変換したskinning変換で骨由来のscaleを反映し、bindposeやRoot Boneのscaleを別途重ね掛けしない。Rendererおよび祖先のobject scaleは、VPからWorldへの既存Transform／frame写像で一度だけ適用し、VP頂点へ追加で焼き込まない。切断面も同じ入力空間へ写し、切断後の物理frameへの配置・表示追従は4.5.6に従う。

Phase 4.65は限定実装・品質と負荷の比較・人間による採否決定までを必須とし、効果がなければ導入見送りを正常な完了結果とする。採用時だけ本体の既存DAG／VPプールへ接続し、以下のJob経路の本体契約、Phase 4.7の未来VP準備、Phase 5.1の人形先行切断統合を適用する。不採用時はこれらを必須範囲から外し、4.7の軌道・Animation計画と5の現在Pose同期切断を残す。人形の先行準備による命中時負荷削減を必達にせず、準備費用・表示開始は本節の通常同期経路に従う。採否は開発時の判断であり、実行時の自動切替・再挑戦や代替の未来Pose同期ベイクを追加しない。判断と理由を本書へ記録し、不採用となった本体設計は削除してGit履歴へ残せる。

未来用Jobは元Mesh由来の不変な頂点属性・weight・bindpose・骨対応を読み込み・登録時などに準備して共有し、候補ごとの取出し・再構築を避ける。共通VP用AoSへ直接出力するか、一時Native出力からJob側で同じ形式へ変換する。ベイク済みUnity Meshの生成・再読取りを挟む義務はなく、頂点数に比例する変換・コピーをMain Threadへ戻さない。Job分割、並列度、入力layout、型・field列、初期対応Asset・変形機能は実装と代表入力の比較で選ぶ。

Pose評価と頂点スキニングを分け、現在Sceneを未来Poseへ変更しない。Pose EvaluatorのBackend選択とMain Threadに残る評価費用は19.3に従い、ProbeのTransform収集方式を必須にしない。同じRig Poseから表示側Jobベイクと骨Physics Proxyの姿勢化へ分岐し、必要なCell・frame等の入力依存は維持するが、描画頂点ベイクだけを理由に物理を待たせない。

VP入力準備と実切断出力を区別し、後者の正負直接配置・転送は4.5.6に従う。入力Pose・sourceデータは読者の寿命まで不変とし、Job出力は4.5.3の範囲所有権と既存の世代・回収規則へ接続する。未完了Jobを翌フレーム固定で強制Completeせず、完了した仕事を回収する。ベイク完了をGPU転送・表示Commitの自動トリガーにせず、準備中は現在表示を維持する。

BakeMeshとのbit単位一致は要求せず、同じ入力Poseと本節のRenderer local／scale規約、有効weight条件で必要属性の誤差と見た目を少数の代表入力で確認する。Qualityによるweight制限の違いや未対応BlendShapeを丸め誤差として扱わず、初期対応範囲を限定できる。Job非対応のweight数・BlendShape・scale構成では未来Job準備を行わず、実命中時に現在Poseの既存同期経路を使う。Job非対応であること自体を異常扱いせず、未来Pose用の同期ベイクや新しい救済経路は追加しない。6.2のcanonical posed position共有、必要属性のfinite性、Topology対応、表示／Stencil／再切断の共用Geometry契約は維持する。参考測定の誤差値を固定上限や実行時の全頂点比較へ転記しない。

実命中時は19章／20.5の既存世代・予測前提・Pose／面条件で準備済み表現・切断成果物を採用する。採用検証のためだけに毎回同期BakeMeshを追加せず、有効な準備済みVP入力・切断成果物を再利用する。準備が未完成または不採用なら、即時表示に必要な入力は現在Poseの同期経路で準備し、そのために投機Jobを強制Completeしない。後着結果は既存規則で採否・回収し、異なる入力Poseの結果を混用しない。初期は即切断開始に必要なCPUベイク・VP変換・転送を命中を処理するフレーム内に収め、同フレームの描画から分離を見せることを目標とする。未準備なら必要な準備後に表示を開始する。表示開始時に残る準備費用を負担するが、この遅延許容を複数フレーム分割の実装要求にはしない。実測前に全対象の同フレーム表示を保証せず、4.5.4の容量拡張時停止は既存の許容として区別する。幾何切断完了より先に仮表示する原則は維持する。

転送は同フレームの描画が更新データを使える順序で発行し、この目標のためにMain ThreadへGPU完了待ちを追加しない。転送発行時間だけを表示開始時間とみなさず、代表入力の準備費用、実際の表示開始フレーム、フレーム全体の90fps目標との両立を既存計測で確認する。

Meshからデータを取得する初期経路はCPU-readableを前提とし、AcquireReadOnlyMeshData等で不要な取出しコピーを抑える。このAPIはAoS変換・GPU転送まで省略せず、Snapshot保持中の元Mesh変更ではコピーが発生し得る。SkinnedMeshRenderer.BakeMesh自体は同期CPU処理であり、自動的な背景Jobとみなさない。その結果は通常Mesh入力として変換へ渡せるが、未来用Job出力にMesh経由を要求しない。0.92だけのためにPose Evaluatorや人形切断を完成させない。

#### 4.5.3 CPUプール・範囲所有権

CPUのVertex／Indexはそれぞれ単一の大きな線形領域とし、Jobへ全域のNativeArray viewを渡す。viewの範囲と実際のアクセス許可範囲を分け、切断・未来用ベイクJobのVPプール対象フィールドにNativeDisableContainerSafetyRestrictionを使用する。container単位の粗い依存判定に代わり、メインスレッド管理の専用アロケータが入力参照寿命と出力予約を所有する。

| 範囲の区分 | 許可するアクセス |
| --- | --- |
| Free | 割当可能 |
| Reserved | 所有Jobだけが読み書きする出力予約。他Job・転送処理は触れない |
| Published | 書込み完了後に公開した読取り専用範囲。複数読者が参照可能 |

VPプールへのJobアクセスは既存Published入力と自分のReserved出力に限る。外部読者へ未公開の出力は、先行Jobの完了回収後にMain ThreadのアロケータがReserved所有権を後続Jobへ移せる。共有書込みは許可せず、完了・回収後に実使用部分をPublishedにし、未使用予約を解除する。再切断等の読者が使うPublished入力は寿命まで保持し、書き換えない。世代・成果物の採否は既存規則で確認し、PublishedをGPU転送の自動トリガーやGeometryState＝Committedとみなさない。安全属性の解除はJob完了確認や実データ依存の解除ではない。

概念上の切断Job入力は全体VB／IB view、入力Geometry参照、新規Vertex／Index書込み許可範囲、出力は結果と実書込み量とする。型・field列・enum数値は固定しない。表示切断に厳密Count→確保→Writeを必須にせず、出力予約不足では範囲外へ書く前に容量不足で終了し、部分成果物を公開せず再予約・再実行できる。7.9の新Index領域も同じ予約規則を使う。形状不正・世代不一致の救済や切断受付の再試行へ広げず、範囲外書込み後の例外回復を設けない。

Published Vertexは上書き・再利用せず、子から継承参照する。移動はInstance Transform／frame写像で扱い、継承のためだけに全頂点を複製・書換えしない。Indexは共有Instance、旧世代Job、転送、投入済み描画等の寿命・実行順序を満たした後に再利用できる。親表示を外しただけでFreeにせず、既存のJob・資源退役へ接続する。通常更新ごとの全Job／GPU待ちは行わない。

0.93の再利用対象は主に退役Indexと、Vertex／Indexの未使用・失敗予約であり、未公開予約の回収はVertex追記方針に反しない。単純な空き領域のbest-effort再利用でよく、最適配置、断片化解消、コンパクションを要求しない。表示Instance／Geometry参照の退役は物理GCとは別であり、0.93へ7.9の生存物体終了を前倒ししない。

#### 4.5.4 CPU仮想予約・GPU容量と転送

CPUはWindows x64のVirtualAllocで大きな仮想アドレス領域を予約し、利用前に必要ページをcommitする方式を基本案とする。予約内では基底アドレスを動かさない。16 GiB等は仮想予約量の例であり、既定容量・初期物理使用量・必須試験規模にしない。仮想予約成功は後続page commit成功を保証せず、予約上限超過・確保不能では開始拒否またはゲーム終了を許容する。

頂点番号は32bitとし、全VBはNativeArray<Vertex>、全IBはNativeArray<uint>のviewを使える。各viewの要素数はint.MaxValue以下、byte数・offset演算は64bitとし、要素数とbyte上限を混同しない。32bit頂点番号を理由にVBを4 GiBへ制限しない。外部所有領域のviewにはConvertExistingDataToNativeArrayとAllocator.None等を使い、分割プールや64bit頂点番号を要求しない。

GPUはPhase 0.92で採用する固定設定の初期容量を確保し、不足時に大きなBufferを作り必要データを移す。新内容を利用できる準備・依存順序を整えてから描画境界で参照を切り替える。旧Bufferを使う描画登録を更新または退役し、旧Bufferを参照する投入済みGPU処理の終了後に一度だけ解放する。Fence方式や新しい状態体系は固定せず、通常更新でのGPU完了待ちを要求しない。この再確保・コピー・切替に伴うSTWを許容し、確保不能・API上限到達では終了を許容する。GPU単一Buffer上限と新旧Bufferの一時共存を考慮し、CPU固定予約とGPU再確保を混同せず、GPUが必ず先に尽きると仮定しない。無停止回復・縮退描画を追加しない。

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

物体固有Motion Vector、XR提出・再投影の新機能、Quest単体Application SpaceWarpを0.9xへ追加しない。仕様・既存実装への必要な破壊的変更と進行中Phase 0.11／別ブランチPhase 0.2への波及は許容するが、Capture、Fixture生成、物理、支持・世代管理の再設計を目的にしない。ZCG v1をRuntime AoSへ作り直さず、旧Asset／Fixtureの一括再生成を要求しない。

#### 4.5.6 正負Indexの直接配置と転送・Commit

通常切断の新規Indexは、一つの連続予約の先頭から正側の全Index、続いて負側の全Indexを隙間なく出力する。各側の生成Capも対応する列へ含める。正負別のallocationや、物理所有者別に後から再集約する工程を設けない。

正負の実使用Index数をn0、n1、予約先頭をbaseとすると、正側は`[base, base+n0)`、負側は`[base+n0, base+n0+n1)`を参照する。片側Geometryが空ならその側の要素数は0とし、paddingやダミーIndexを置かない。末尾の未使用予約は初期化・転送せず既存アロケータで解除する。負側開始位置に必要なn0は切断内の件数計算や出力順序で決め、別Count JobとMain Thread往復を必須にしない。切断・Cap生成の作業領域と予約不足時の4.5.3の扱いは維持する。

正負両Geometryが一つの物理所有者へ付く場合も、二つの部分範囲参照をそのまま同じ所有者へ対応付け、一所有者一参照へ統合しない。Material・frame等に必要な描画分割は残し、連続配置や1回の転送を全Passでの厳密1 drawと同一視しない。

継承Vertexは既存番号を使い、新規交点・Cap頂点等だけを追記する。Triangle切断、正負への振り分け、必要な非交差IndexコピーとTopology参照更新は通常の出力生成に含める。面集合、Triangle内頂点順、属性、windingを保存し、表示・Stencil・再切断は同じIndex正本を使用する。

新規Index列は実使用範囲`[base, base+n0+n1)`だけを一度のSetDataでGPUへ送る。正負別の2回転送、未使用予約の転送、中間配置の先行転送と後の再転送を行わない。対象全体のGeometryが無変更で既存GPU Index範囲をそのまま再利用できる場合は、新規Index書込み・当該切断のIndex SetDataは0回とする。受付前No-opもこれに含む。1物体結果でもGeometryが変わる場合や、Triangle非交差でも正負配分に新Indexが必要な場合は省略しない。

この1回／0回は通常切断の当該新規Index出力だけの転送回数である。Vertex、Descriptor、初回Mesh→VP準備、GPU容量拡張時の再転送は別とする。先行準備で転送済みの有効な出力は再利用し、命中だけを理由に再転送しない。未転送入力範囲の継承時に必要な転送は、GPU転送済み範囲の再利用による0回と区別する。

正負Indexの配置はFinal採否によって変えない。有効なCPU範囲の完成後は、配置確定のためだけにcookや最終物理採否を待たず転送できる。現在の有効な追従先／frame、世代と転送順序が揃えば描画境界でGeometry Commitし、後の物理変更は参照の対応付けで扱う。CPUのPublished、GPU転送、表示Committed、物理Commitを同義にしない。未転送の継承Vertexも既存の必要範囲管理で扱う。

物理は必要なCPU情報と物理成果物が揃えば既存の安全な境界で先に適用し、表示転送を待たない。表示も物理成功を先取りせず、現在有効な構成へ追従する。Geometry Commitまでは5章の選択平面・Cap上限・Camera近傍等の規則で仮表示を継続する。両用途のGeometry Commit後に対応Temporary描画だけを回収し、残る物理工程のPendingを維持する。

LogicalCutOperation公開へGPU転送・Geometry Commitを条件追加せず、公開済みのCPU子Geometryは物理未完了でも再切断できる。世代失効した旧結果を表示待ち解消だけのために公開しない。旧Indexは既存Job・GPU・共有参照寿命後にFreeし、通常更新で全Job／GPUを待たない。Stable Unsplitでは採用済み構成へ追従し、来ないFinal成功を待たない。ProvisionalFaultFrozen、確定空、形状不正、Operation公開前終端の扱いは既存の維持・非表示・回収規則へ従う。

Phase 5.6の追加分割で必要になる面の配分、新Index領域への振り分けコピー・転送・旧範囲退役は7.9へ置く。通常切断へ全島列挙を前倒ししない。配置・転送回数は既存計測で確認し、新しいRuntime監視、品質Gate、数値SLAを追加しない。

## 5. 即時表示レンダラ

即切断と実切断後の表示／Stencilは4.5の同じVP Geometryを使用する。本章の即時表示は必要なベイク・VP変換後に開始し、有効な先行準備を再利用する。実切断CPU結果完成後も4.5.6の仕上げ・Geometry Commitまでは同じ規則で継続する。Cap単位compaction／部分更新の禁止はStencil描画最適化の制限であり、グローバルプールの必要な更新・拡張を禁止しない。

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

候補は既存のPending Cut列とCutBoundary Record公開列を受付の古い順に走査し、未Commit祖先制約を子孫制約より必ず先に置くstable順で選ぶ。Pending CutからOperation由来Recordへ移る際も同じ受付位置を保ち、同じ切断面を重複登録しない。選択結果は候補列の先頭から最大12面のdependency-closed prefixとし、ある子孫境界を選ぶために必要な未Commit祖先境界が選択外なら、その子孫も選ばない。通常の公開処理は祖先を子孫より先に列へ追加する不変条件を持ち、復元データがこの順序を満たさない場合は新しい順へ並べ替えず、違反境界以降をIgnoredとして背景Geometry完成へ委ねる。ID値によるsortや別の優先度Metadataを追加せず、左右眼、Color、Depth、ShadowCaster、Stencil Volumeの全Passで同じ選択結果を共有する。カメラ距離、眼、Pass、毎フレームの可視性で順序を変えない。候補追加、LogicalCutOperation公開、Geometry Commit、RenderFragmentとCutBoundaryの対応関係変更のいずれかが候補資格または依存関係を変えた場合、状態変更を公開する同じ描画更新境界で再構築する。

D3D11／Shader Model 5のPoC Profileは`RasterClipPlaneCapacity = 8`、`PixelClipPlaneCapacity = 4`、`TemporaryClipPlaneCapacity = 12`を初期値とする。`SV_ClipDistance`と`SV_CullDistance`の合計component上限8をRaster側の正本とし、このShader Variantでは`SV_CullDistance`を使用しない。先頭8面をVertex Shaderから`SV_ClipDistance0/1`の合計8 componentへ出力し、未使用componentは全頂点で正の有限値へ固定する。続く最大4面だけを固定長per-instance配列と`PixelClipCount`からPixel Shaderの`clip()`で評価する。面数や平面値によるMaterial、Keyword、Pass、Draw分割、可変長Buffer、動的Loop上限の増加を行わない。MSAA時は先頭8面のRasterizer clippingによるcoverageを正本とし、Pixel fallback境界との微小なedge品質差は短時間の品質低下として許容する。

dependency-closed prefixへ入らない後発Pending Cut／境界は`IgnoredTemporaryClipBoundarySet`とし、即時RendererのColor／Depth／Shadow／Stencil Volume入力からだけ除外する。Pending Cutまたは公開済みCutBoundaryRecord、Geometry状態、Logical Fragment、切断履歴、世代、点Anchor配分と所有者単位の固定／動的、共用Geometry／Convex Job、物理Commit、優先度付けは変更・破棄せず、背景Geometry Commitで正しい形状へ収束させる。無視された新しい面は一時的に即時表示されず、影もその面より前の形状となり得るが、選択済み祖先の外側にGeometryを復活させずSiblingを重ねないbounded degradationとする。Plane overflowを理由に既存Jobをcancel、再発行、同期完了してはならない。

`TemporaryRenderCapRecordSet`、Cap Bounds Polygon、StencilのColor割当て、Draw ListはPlane overflow時にもcompaction／部分更新しない。Ignored境界に対応するStencil Volume Recordだけをsubmitせず、Cap板Recordは従来Batchへ残してよい。対応Volumeも別RecordのResidual Stencilもないsampleは初期値128のままなので板はColor／Depthを書かない。通常Colorでは別RecordのResidual Stencilが到達し得る非互換Capを分離し、最後の統合Colorでは混入、欠落、余計なCapおよび誤ったDepthを許容する。Ignored Capを隠すための追加Mesh生成、有効フラグ、Buffer compaction、個別Draw除去、代替VFXは追加しない。

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
9. Camera内部またはNear Plane近傍では、既存の軽量Bounds判定と安価なFade／Vignette／Maskの過剰反応・見逃し、部分Cap、欠落、余計なCap、内部面、Stencil混入、左右眼差を許容する。

注1、3～6は契約内の向き構成、注2は入力拒否、注7はカウント範囲外、注8はColor統合、注9はCamera近傍だけの取扱いである。共用Geometry契約に反する入力を通常経路へ投入する許可や、通常の正向き閉Shellでの欠落・混入の一般許容へ拡張しない。仮Capの品質例外は共用Geometryの生成、検証、Commit契約を緩和しない。

### 5.3 断面マテリアルとデバッグ表示

通常断面は、全体のポップなトゥーン表現と同じ共通シェーダーへ、粘土を思わせる彩度の低いグレーをBase Colorとして渡す。断面専用のトライプラナー、ノイズ、凹凸、内部グラデーション、写実的な特殊シェーダーは使用しない。仮断面と実断面は生成方法が異なるが、通常表示時の陰影段数、輪郭、ライト応答、グレー色を一致させ、差し替えを目立たせない。

デバッグモードでは同じトゥーンシェーダーのBase Colorだけを処理経路に応じて上書きする。Unlit化はせず、断面の向きと立体感を維持する。

| 断面色／表示 | 意味 |
| --- | --- |
| 赤 | 即時レンダラの仮断面が現在表示中 |
| 青 | 命中前に完成した先行切断成果物が、FrontEdge命中時の検証に成功してCommit済み |
| 緑 | SlashFront命中後に切断計算を開始し、VP GeometryへCommit済み |
| 水色 | 先行成果物の一部を再利用し、命中後に残りを完成してCommit済み |
| 黄（工程情報） | 表示切断CPU成果物は完成したが、必要転送・現在の参照／frameの確定・表示Commitを待っている。表示採用済みを意味しない |
| オレンジ | 計算予算超過、タイムアウト、簡易形状など品質低下フォールバック |
| 紫の点滅 | 予測不一致またはGeneration不一致で先行成果物をReject |
| 黒い縞／縁 | 表示形状とColliderが一時的に不一致 |
| 通常グレー | 通常表示。Temporary Renderer対象がないStable表示にも使用 |

黄の待機中も仮Capの主色は赤とし、黄の工程情報は既存の縁表示または選択対象パネルで識別できればよい。黄色表示用の中間Geometry転送や専用Rendererを作らない。

赤は現在の仮表示状態、青／緑／水色は最終成果物の計算経路を表すため、典型的には`赤 -> 青／緑／水色 -> 通常グレー`と遷移する。経路解析モードではStable後も青／緑／水色を保持できるようにし、通常の状態確認モードではStable移行後に色上書きを解除する。通常グレーは「静止」を意味せず、破片が運動中でも表示と物理が確定していれば使用する。

色だけへ依存せず、Reject、Pending Physics Split、品質低下には点滅、縞、縁取りを併用する。詳細文字は全断面へ常時描画せず、選択中の1対象だけを単一の画面／手首固定デバッグパネルへ5～10Hz程度で表示する。全件の詳細はEditor Timelineと保存Traceを正本とする。

### 5.4 即時切断中のShadow Map

影はRealtime Shadow Mapを使用する。即時切断中の論理破片はShadowCaster PassでもカラーPassと同じper-instance切断平面、論理破片Side、分離Offsetを適用する。一方、Shadow Mapには色付き断面を描く必要がないため、Stencilによる仮断面キャップは生成せず、ShadowCasterだけを両面描画して開口の奥にある外殻裏面を遮蔽面として使用する。

この方式は、閉じた元形状に対する外部Shadowの被覆範囲を低コストで近似するが、本来は切断面キャップが書くはずの深度より奥側の外殻深度がShadow Mapへ入る場合がある。切断面が床／壁に近い場合、薄い物体、非閉形状、Self Shadow、Shadow Bias、Cascade境界等では接地影の浮きや漏れが発生し得る。即時状態の短時間近似として許容し、4.5.6のGeometry Commit後は実断面を含む閉形状と片面ShadowCasterへ戻す。

- `Cull`はper-instance属性ではなく描画状態として扱い、Shadow描画を原則としてStable片面群の`Cull Back`とPending両面群の`Cull Off`へ分ける。UnityのRenderer経路では`ShadowCastingMode.On`／`TwoSided`、専用Renderer経路では対応するShadowCaster Variantを使用する。

- 「2回」はShadow Map全体が必ず2 Draw Callだけになる意味ではない。Light、Cascade／Shadow Map Slice、Mesh、Material、Shader VariantなどのBatch単位ごとに、少なくとも片面群と両面群へ分かれるという意味とする。

- 切断平面は5.2の固定上限Instance Recordに`RasterClipPlaneCount`、`RasterClipPlanes[8]`、`PixelClipPlaneCount`、`PixelClipPlanes[4]`、各面へ反映済みのFragment Side、`SeparationOffset`として保持する。ShadowCasterもColor Passと同一のstable選択結果を使い、先頭8面は`SV_ClipDistance`、続く最大4面はPixel Shader `clip()`、超過面は即時Shadowから無視する。切断数や平面値でMaterial、Shader Keyword、Passを増やさず、同じCull群のBatchを維持する。

- Stable Instanceをclip対応Shadow Shaderへ統合するか、`RasterClipPlaneCount = 0 && PixelClipPlaneCount = 0`専用の高速経路へ分けるかは実測で決める。全ShadowCasterを常時`Cull Off`にしてDraw群を統合する案は、裏面Raster／overdraw増加を測定せず採用しない。

### 5.5 コスト制御

- 同一物体のTemporaryRenderCapRecordSet件数の初期目標は実際に描くCap Record 2～4枚とし、固定側も数える。Geometry Commit済みRecordは数えない。

- Cap Record 2～4枚は通常時に背景再構築を促す品質／費用目標であり、`TemporaryClipPlaneCapacity = 12`はShader処理量を固定する絶対安全上限である。1枚のCap Polygonまたは1個のRenderFragmentが、ほかの未Commit境界による複数の半空間制約を受けるため両者の件数は同義ではない。通常目標を超えた瞬間に同期再構築せず、Raster 8面、Pixel 4面、残り無視という固定処理量を維持する。

- 目標到達時は未Commit切断を含むStable VP Geometryを背景で生成する。4.5.6の両用途の参照公開とCommitted遷移の成功後だけ対応Cap Recordを外し、切断履歴は保持する。

- `RasterClipPlaneCount`、`PixelClipPlaneCount`、`IgnoredTemporaryClipBoundaryCount`をProfiler Counterと選択対象のデバッグ表示へ出す。Plane overflowによってGeometry Jobの優先度、依存関係、cancel／再発行規則を変更せず、Frame内の待機や同期Commitを禁止する。

- 画面外・遠距離・停止中の物体を優先的に確定する。

- Stencilは切断面ごとの一時作業領域として再利用し、恒久的なビット割当は行わない。

### 5.6 スクリーンスペースStencil Batch

Stencil Bufferは画面座標ごとに共有されるため、すべての即時切断物体を無条件に同じ通常Colorへ蓄積しない。現在の全World Cut Plane、各PlaneのFragment Side／半空間、分離Offset、Cap Material、法線、デバッグ色、Fade等が一致する対象を`CapCompatibilityKey`で同じ互換Groupへまとめる。6章の共通入力Gateに合格したGeometryは正、負、混合符号を分離条件にせず、入力Geometryの向きを保存して符号付き加算する。互換Groupの意味は厳密な幾何Unionではなく`sum(W_i) > 0`であり、各寄与が正しく得られて最終値が符号判定範囲内なら`{sum(W_i) > 0} ⊆ union_i {W_i > 0}`となる。正逆相殺やColor分割によるMask差は5.2の品質例外へ限定する。

StencilはParityの`Invert`や飽和演算ではなく、共通入力Gateに合格したGeometryのFront／Back Faceに対する`IncrementWrap／DecrementWrap`からなる8bit Winding Count方式を使う。基準値を`B = 128`、符号付きWindingを`W`、格納値を`S = (B + W) mod 256`とし、各Color開始時とColor間で専用Stencil Byteの全8bitを128へ初期化する。Rect描画で初期化する場合は`Ref 128`と`Replace`を使う。Capは`S > 128`だけを描画し、Unity ShaderLabでは`Ref 128 / Comp Less / ReadMask 255`とする。Counterは`ReadMask 255 / WriteMask 255`、Cap側のStencil書込みは無効とし、`S <= 128`のsampleはColorもDepthも書かない。裏面透明という通常Color方針をStencil集計からBack Faceを除外する意味にせず、Front／Back双方を対称なclip／Depth条件で扱う。Rasterizer／Transform補正は通常Colorのfront-face規約と一致させ、Geometryの向きをPositiveへ直す補正や二重補正を行わない。

最終Windingが`-127 <= W <= 127`なら`S > 128`は`W > 0`と一致するが、この範囲の証明、検査、監視、補正は行わない。途中のWrapは正常な演算として扱い、Mesh数、Triangle数、Draw数、途中累積値の上限として解釈しない。Count容量によるBatch分割、Fallback、別形式Counterを設けず、範囲外でも同じ8bit演算と比較を続け、結果は5.2の品質例外とする。

`Residual Stencil Support`は集計後に`S != 128`となる画面領域を表す論理概念であり、実Stencilから検出・再構成・監視する対象ではない。負残留も別Recordの正寄与を打ち消し得るため、通常Colorの競合判定から除外しない。通常Colorでは既存の物体OBBと可視Cap Boundsの左右眼投影をResidual Supportの保守的な重複判定に使い、非互換対象を別Colorへ分離する。Geometry入力条件は5.7で上流が確定し、描画ごとに再検証しない。

`Stencil Conflict Graph`は分離が必要な対象間の関係を表す論理モデルに限る。全Graphの構築・保存、全組合せ走査、stable順、Greedy Coloring、最小彩色、異なる方式で同じColor番号を得ることを要求しない。通常Colorの左右眼分離条件、Color上限、各Color内の全Volume後に全Capを描く順序を満たす範囲で、候補検索、データ構造、Color割当て方式を実測から選ぶ。

描画するColor数は固定上限`MaxStencilColors >= 1`で制限する。先頭の最大`MaxStencilColors - 1`個を通常Colorとし、配置できない対象は最後のColorへ直接まとめる。最後のColorでは非互換対象の分離を要求せず、5.2の品質例外を適用する。`MaxStencilColors = 1`では全対象を最後のColorへ入れる。上限外Colorの生成、遠距離／小画面Capの省略、代替VFX、Job優先度変更、再彩色による救済を要求しない。GPU時間はProfiler等で測定・調整する性能目標であり、厳密な実行時上限、監視制御、多段Fallbackは設けない。

- Broadphaseでは分離Offsetと安全Marginを含む物体OBBの左右眼投影矩形を使う。重なる組だけ、表向きのOBB切断面から得たCap Bounds Polygonを左右眼へ投影して再判定する。どちらの判定も非交差なら安全という悲観的な証明として扱い、Near Plane交差、Raster／MSAA、頭部移動誤差を考慮してBoundsを保守的に拡張する。

- `CapCompatibilityKey`は順序を正規化した表示対象`CutPlaneId`列、Side Mask、Offset、Material／Debug／Fade状態から作り、Raw floatだけをHashの正本にしない。同じSlash由来でも、19.5.1の対象別リベースまたは対象の移動・回転によってWorld Planeが異なり得るため、現在の操作World Planeをepsilon比較し、一致しなければ別Groupへ分離する。片方だけに追加Temporary Render Boundaryがある場合も互換ではない。符号分類とWinding容量はKeyへ含めない。

- `LogicalCutOperation`公開前の即時Capは、親`RenderFragment × Pending Cutの採用面`から導出し、確定した論理子や境界を先取りしない。公開後のキャップの幾何可視性は元Object単位ではなく、`論理破片 × 切断面`の`CapRecord`単位で判定する。同じ切断面でも正負破片の断面Normalは逆向きになるため、片側が裏向きでも反対側を自動的に省略しない。支持による描画省略を行わない。

- LogicalCutOperationは通常処理で正負各最大1の非空直接子と実在する切断面参照が確定した後に一度だけ構築・公開する。CutOperationId、ParentLogicalFragmentId、ParentObjectGeneration、直接子ID、CutBoundaryIdと採用面・Side・Geometry参照の対応を保持する。非空とはGeometryまたはPhysics Convexが残る側を含み、両方空の側にダミー子を作らない。各Sideに存在するGeometryと実Capは、そのSideを保って維持する。両SideのGeometryが同じ物理所有者へ所属してもこの規則は変わらず、空側へGeometryやCapを補わない。

- DirectChildCountは1または2とし、正負各最大1とする。CutBoundaryCountは既存の0～256、IDは0を予約した正の32bit intとし、CutOperationId／LogicalFragmentLocalId／CutBoundaryLocalIdはObjectIdの寿命中に種別ごとに非再利用とする。ParentObjectGenerationはuint全域とし入力Snapshotと一致させる。親と子、子同士、境界同士のID重複、未知参照、世代不一致、件数上限違反は原子的にRejectする。

- CutBoundaryRecordは再切断と表示に必要な採用面・Side・子Geometry／frame参照を表す。島の接続Edgeや異なる二物理所有者を必須にせず、複数Contour／Capを一つのLoopへ潰さない。確定空側には架空参照を作らず、子1件や境界0件を正常結果として扱う。公開済み参照と切断履歴は既存の世代・資源寿命へ従う。

- `TemporaryRenderCapRecordSet`内の現在のWorld Cap Planeについて`dot(CapNormal, EyePosition - CapPoint)`を左右眼で評価する。両眼とも明確に裏向きのCapRecordを幾何不可視とし、片眼だけ表向きならSingle Pass Instanced用Recordを残す。互換Group内に幾何可視な実描画Capが一つもない場合も、Stencil Clear／Volume／Cap処理を丸ごと省略する。

- カメラが切断面近傍にある場合の左右眼不一致と頭部微動による点滅を避けるため、Facing epsilonと1～2フレーム相当のヒステリシスを候補とする。Frustum外判定も同じ段階で行うが、通常のclip済み破片カラー描画とShadowCasterは消さない。

- Cap処理は`受付済み未Commit面・SideからRecord構築 -> TemporaryClipConstraintCandidateSetのstable選択 -> 両眼Frustum／Facing Cull -> CapCompatibility Group -> 全Cap不可視Group Cull -> 通常Color割当て／最後のColor統合 -> Colorごとの128初期化／全Volume／全Cap描画`の順とする。Operation公開時に同じ採用面のRecordへ一度だけ引き継ぐ。同じColor内のVolume→Cap順を維持し、Plane容量超過でCap Record集合や論理状態を変更しない。

- Camera内部とNear Plane近傍は、既存の候補列挙、登録済みOccupancy、OBB／Box／Capsule等の軽量BoundsとNear Plane交差判定だけで検出し、既存描画経路による暗転、単純なVignette、単色Mask等で隠す。共用Geometryや実Triangleの内外・交差走査、専用Depth／Normal Pass、Blur、多段Post Process、Stencil修復、完全分離、Camera／物体の強制移動、Job取消／再発行、同期Mesh切断、物理／Geometry Commit取消を追加しない。検出漏れと隠し切れない破綻は5.2の品質例外とし、Stable Geometry置換後はTemporary Stencil由来の部分Capを残さない。

- 仮Cap処理中にStencil Byte全8bitを排他的に使える構成だけを対応対象とする。Renderer初期化時に既知の設定と必要なAttachmentを一度確認し、成立しなければ構成エラーを示してゲームを開始しない。部分Bit利用、Stencilなしで継続する代替経路、復旧状態機械、毎フレームの構成再検証は作らない。既存Depth／Stencil Attachmentで成立する場合は別Textureの確保を要求しない。Stencil Byteの恒久的な物体割当は行わない。

### 5.7 共用Geometry・更新・描画の責務境界

前処理または切断対象登録は6章の共用入力契約を一度だけ検証し、合格したGeometryだけを表示／Stencilの共通正本として公開する。切断・生成側は変更領域と生成Capについて同じ不変条件を維持し、4.5.6の仕上げ後に両用途へ一つのGeometry Commitとして公開する。不正な個別成果物は両用途ともCommitせず、受付済み切断が進行可能な間は利用可能な最後の共用Geometryと既存のPending Clip表示を維持する。Operation公開前の切断を既存規則で終端失敗とした場合だけ4.2の限定退役を適用する。用途別の適否、修復、別Mesh、同期完成待ちを追加しない。

点Anchor集合と、そこから導出される所有者の固定／動的、描画対象、選択済み切断平面、ローカルCap Boundsはそれぞれの入力変更時に担当側が更新する。同じフレームでの参照公開、世代・Commit条件を維持し、毎描画で切断履歴を再評価しない。

描画側は公開済みの共用Geometryと選択平面を使用し、Geometryや切断履歴の再検証、不成立原因の再検出、修復方法の選択、実Stencilの漏れ・相殺成否の監視、Triangle／Edge走査による別判定を行わない。現在のView、Transform、World Plane、投影Bounds、Facing、描画状態に依存する処理だけを描画時に行う。

入力不正の検出は入力準備側、出力不変条件は切断・生成側で一度だけ試験し、同じ不正条件をStencil描画、GPU画像比較、両眼試験で反復しない。契約内入力の符号・比較・描画条件はStencil実装時に小さいFixtureで確認し、状態公開・参照切替・世代／Commitは担当側で確認する。汎用Validator／Cache Framework、新しい状態機械、範囲外Windingのstress試験を追加しない。

7.9の確定後の任意分割は、既存の面集合とTopologyを配分し、所有者・frame・参照を同じ公開境界で切り替える。表示／Stencilの共用契約は維持し、新しい共用Geometryの切断面・Capや描画側の再検証を追加しない。

## 6. 表示／Stencil共用メッシュ切断

### 6.1 共用Geometryの正本

切断対象では、表示とStencil Volumeが同じGeometry世代、表面／実Cap Triangle集合、winding、Topology正本を参照する。現在の共用Geometryは4.5のUnity MeshまたはCPU側VPを正本とし、VP移行後はComponent単位の複数Geometry参照で構成できる。用途別の基底／派生Mesh、Index巻き替え、閉鎖面、Cache、Commit状態を持たず、4.5.6の正負Indexを直接出力し、同じVP正本からGPUコピーを更新する。分類、交点計算、切断、Cap生成を用途ごとに再実行しない。物理Convexはこの統合の対象外とする。

| 区分 | 内容 |
| --- | --- |
| 入力 | posed頂点、法線、接線、UV、色、submesh、index、切断平面、`RenderCutTopologyMap`、論理破片ID、世代番号、`RenderCutRobustnessProfile` |
| 処理 | Topology Vertex単位の分類、Original Edge単位の交点共有、元surface順序を保つTriangle clip、Topology由来Contour接続、有向Cap生成 |
| 出力 | 正負の共用Geometry、断面submesh、Bounds、幾何切断に必要なTopology Metadata、Commit Metadata |

複数Renderer、submesh、閉Componentを一つの巨大Geometryへ結合する義務はない。幾何処理に必要なComponent識別を保ち、Geometry参照と描画集約、LogicalFragment、物理所有単位を分離する（4.5.1）。属性seamによるRender Vertex分裂、非同期Jobのための世代保持、CPU／GPU表現、物理Convexも別Geometryとは扱わない。

### 6.2 共通入力契約

`RenderCutTopologyMap`が表すTopologyについて、各Edgeへちょうど2面が接続し、その2面が共有Edgeを互いに逆方向へたどること、各Topology Vertexの周囲の面が一つの閉じたfanを作ることを要求する。使用するposition／属性はfinite、index／submesh／Topology参照は有効とし、同じTopology Vertexのposed positionと同じOriginal Edgeの交点positionは一度生成したcanonical値を共有する。

Disconnectedな閉Component、全体反転した閉Component、bind pose／skinning後のSelf-intersection、別Topology Component間のIntersection／Overlap、Internal／Nested Shell、別Topologyとして表現されたCoincident／Duplicate Componentを許容する。物体全体を単一連結成分にせず、座標一致を理由に別Componentをweldしない。一方、Boundary Edge、3面以上が共有するEdge、一つのTopology Vertexを共有する複数fan、局所winding不整合は共用入力として受理しない。

この契約は閉じた向き整合Topologyの契約であり、自己交差のない幾何Solidの証明ではない。全Mesh自己交差、inside／outside、Generalized Winding Number、signed volume、外向き判定、向き正規化を入力Gateへ追加しない。

基底AssetはImport／前処理または切断対象登録時に一度だけ検証し、合格後は切断側が不変条件を継承する。UV／Normal／Material seamはFBX control point等の由来Topologyで対応付ける。由来が不明なseamを位置探索で推測したり、開放Boundaryのまま受理しない。不合格入力は切断可能Geometryとして登録せず、Runtime修復、Stencil専用Shell、表示だけを許す切断経路へ降格しない。必要なAsset修正や前処理Recipeをすべて自動実装する義務はなく、修正しない入力は切断対象外にできる。

### 6.3 Runtimeの面積0 Triangle

Runtimeの共用Geometryは面積0のTriangleを通常の入力・成功出力として保持できる。面積0だけを理由に面やedge-useを除去、非寄与化、修復、Commit拒否せず、閉鎖・edge／vertex manifold・局所winding整合を論理Topologyで維持したまま表示、Stencil、再切断へ使用する。

退化面の属性はfiniteに保つが、定義できない幾何法線の正規化を要求しない。アセット前処理、ZantetsuCanonicalGeometry v1、Synthetic Fixture Validator、Physics Convexの非退化条件は変更せず、スキニング後または切断後のRuntime Geometryへ再適用しない。

### 6.4 切断とCap生成

Cを6.2と6.3のRuntime共通契約とすると、切断処理は次を満たす。

```text
C(入力Geometry) かつ 切断処理成功
    ⇒ 公開する各出力Geometryについて C(出力Geometry)
```

これはRuntime証明器や全Mesh再走査を追加する要求ではない。切断・出力生成工程内で変更したOriginal Edge、生成Edge、Cap、finite性、件数を確認し、未変更領域は前世代の不変条件を継承する。Topology Vertex単位のsigned distanceを一度だけ確定し、OnPlaneはPositive側へ所有させて同じ分類を全incident Triangleで共有する。全頂点OnPlaneのTriangleはPositive側へ1回だけ保持し、そのTriangleからCap segmentを生成しない。平面がvertex／edge／faceを通る場合、同一点に複数のcut portが生じる場合、極小／面積0 Triangle、契約内Self-intersectionを通常ケースとして扱い、別Topology由来のportを位置近傍だけで接続しない。

各出力の元surfaceが持つ切断境界Half-edgeに対し、Capは逆方向の境界Half-edgeを持つ。切断Boundary EdgeにはCap側の面をちょうど1枚接続し、Cap内部Edgeには互いに逆方向の2面を接続して、頂点周囲も閉じた単一fanにする。非退化CapのNormal／Tangentはこのwindingと一致させる。切断平面は位置、signed distance、射影、UV basis等へ使用できるが、実Capの表裏は元surfaceの有向境界から決め、全体反転した入力の向きを作り直さない。

Half-edge、edge hash、圧縮adjacency、Contour表現、三角形化、局所交差処理は、共通契約を満たす範囲で実装と実測から選ぶ。単純なContourへfan等を使うことは禁止しないが、不正入力や不正出力を重複Cap、逆向き重複面、Open Chain封鎖、Non-manifold lane分解で救済する段階列は要求しない。異なる閉ComponentやTrackをBoolean Unionせず、契約内の自己交差処理に必要なら局所Arrangementを共通Kernel内で使用できる。

### 6.5 公開と失敗

切断結果は4.5.6の直接出力・必要転送と公開条件を満たした後、表示とStencilへ同じGeometry Commitで公開する。待ち合わせは正常な未完了であり、失敗とは扱わない。Generation、参照、所有権、件数・容量、変更部の共通契約またはfinite性に失敗した結果は両用途とも公開せず、通常の成果物回収へ流す。受付済み切断が進行可能な間は、利用可能な最後の共用Geometryと既に公開済みのPending Clip状態を維持し、表示だけ新結果、Stencilだけ旧結果というGeometry分裂を作らない。`LogicalCutOperation`未公開の切断を既存規則で終端失敗とした場合だけ4.2の限定退役を適用し、先行する有効なPending制約と履歴を退役させない。

表示の出力予約不足だけは4.5.3の非公開終了→再予約→再実行を許容する。形状不正・世代不一致や切断受付の再試行へ広げず、失敗用Snapshot、復旧State、用途別Mesh、同期切断、その他の再試行段階、簡易表示Proxyを追加しない。容量限界の扱いは4.5.4に従う。契約内の通常切断を恒常的に拒否して成功扱いにはしないが、失敗時に必ず代替Geometryを完成させる保証も置かない。物理・支持のCommit単位、Actor状態、世代・資源寿命は変更しない。

## 7. 物理切断

### 7.1 一時状態

ColliderのBake／cookingは即切断表示開始と初回仮運動のクリティカルパスに含めない。必要なベイク・VP変換後にclipと仮断面を表示し、入力Cellの点Anchorを7.1の規則で配分でき、容量と構築条件を満たせば、旧cook済みConvexを再利用するProvisional Rigidbodyを正負各最大1生成する。固定側も描画するが動かさない。Anchor配分等の必要なJob未完了は既存Work依存で待ち、世代不一致・不正入力は既存検証で不採用にする。Actor／Shape／Constraint容量や原子的構築の不成立では有効な旧物理を維持し、部分的なProvisionalを公開しない。

- 刀は旧Colliderを含む物理Colliderへ接触させず、Edge Direction Gate成立中の論理SweepだけでHitを判定する。プレイヤーの手・身体も初期仕様ではプロップ／破片へ接触Impulseを与えず、移動制限と視界保護は7.2.3の非接触Locomotion経路で扱う。

- 配分されたConvex／CellのAnchorが一つでもあればその所有者全体を固定し、なければ動的とする。両側AnchorなしはProvisionalPhysicsSplit、固定側を含む場合はProvisionalAnchoredSplitとして、安全な物理境界で各側最大1 Actorを原子的に作る。固定側はStaticまたはKinematicとし、固定用の外部Joint／Constraintを生成・継承しない。建物由来子のMetadataとD6は7.2.2に従う。構築前提を満たさない場合はPendingPhysicsSplit／PendingAnchoredSplitで旧物理を維持する。

- Provisional Shapeの配分、OBB質量近似、Separation Constraintは19.5.1の確定操作Local Planeを共通入力とする。予測Planeを採用済みなら、未完成Meshを理由にSourceSlashPlaneから別の配分を作らない。Provisional Shapeは元Actorのcook済みConvex Geometryとlocal poseを共有参照し、Provisional生成のためのConvex切断、Mesh複製、`Physics.BakeMesh`を行わない。受付前No-opとProvisional配分は7.6の同じ現在採用Convex集合、Snapshot、面、epsilonによる分類を共有する。各Convexの全頂点が片側ならその側だけへ、平面と交差するかepsilon内／分類不能なら両側へ配分候補を置く。連続切断では現在Provisional Actorが参照するShape集合へ同じ規則を適用し、Geometry Resourceを再利用する。Geometry共有がBackend上で安全に成立しない場合は同期cookへ落とさず単一FragmentGroupを正式採用できる。

- 共有Geometryごとに`ProvisionalCollisionResourceLease`を取得してからShape Instanceを構築し、全Actor／Shape／Constraint／Lease取得成功後だけ新物理状態を公開する。失敗時は逆順rollbackし、旧Actorを維持する。Final Shape交換または新世代Provisional置換では旧ShapeをPhysics Sceneから除去し、参照する全Actorの物理ステップが完了した後だけLeaseを返す。最後のLease返却前にCooked Geometryを破棄せず、連続切断、Generation Reject、Timeoutでも二重返却しない。

- 同じProvisional分裂系譜のSibling Actor間はCollision responseを無効化するが、それ以外の静的／動的WorldとのCollisionは通常どおり全て有効にする。交差Convexの複製により、表示より早い接触、Sibling側へ張り出すGhost Contact、同じ外部物体から複数Actorへの接触、短時間のImpulse重複を許容する。これらを切断失敗またはSolverへ投入禁止な初期penetrationとはみなさず、非finite state、Profile上限を超える速度／角速度、Constraint破綻だけを公開後Fault Frozen対象とする。

- 同じ切断で生じたProvisional Sibling間には、相対回転と切断面接線2軸を保持し、法線方向の分離を許可しつつ切断直後より深い再侵入を防ぐ短命な`ProvisionalSeparationConstraint`を持たせる。D6 Joint、速度射影、相対Pose補正等の実装はT-091で比較し、PoCは最も単純で安定する方式を選ぶ。Constraint生成失敗時に一部Actorだけを公開しない。

- 公開済みProvisional GroupはGroup単位の固定容量二重Buffer`ProvisionalLastFinitePhysicsSnapshot`を持つ。各Slot Headerは`ObjectId`、`ObjectGeneration`、既存物理Clockの`FixedStepId`、`ActorCount`を、Entryは対応する正の`LogicalFragmentLocalId`昇順のWorld pose、COM線速度、角速度を持つ。Fixed Step完了後に非公開Staging Slotへ全Actorを同じStepから収集し、Actor集合、ID順、世代、件数、全数finiteを検証した後だけ、公開Slot indexを1回のatomic storeで切り替える。途中失敗、Fault検出、Actor集合変更、世代変更ではStagingを破棄して直前の完全な公開Slotを維持し、Actorごとの部分更新を公開しない。Provisional Actor集合の初回公開も全Actor分のStep境界Snapshot作成成功と同じ原子的Commitへ含め、2 Slotは同じGroup内で交互に使うがGroup破棄までは別Groupへ再割当しない。

- いずれかのActorで非finite state、線速度／角速度のProfile上限超過、またはConstraint runtime破綻を検出した場合は、対応`LogicalFragmentLocalId`昇順に全Faultを収集し、固定優先順位`NonFiniteActorState > ConstraintRuntimeFailed > LinearVelocityLimitExceeded > AngularVelocityLimitExceeded`、同順位では最小LogicalFragmentLocalIdの原因をPrimary Faultとする。進行中のSnapshot Stagingを必ず破棄し、次の物理ステップ境界でGroup全体を最後に公開済みの完全なGroup Snapshotへ復元して`ProvisionalFaultFrozen`へexactly onceで遷移させる。全Sibling Constraintを外し、全ActorをKinematic化し、速度／角速度0、蓄積Force／Torque消去をGroup単位で原子的に行う。Faultを検出した現StepのActor値は、全数finiteに見える場合でも封じ込め入力に使用しない。完全Snapshotがない、または全Actorの原子的封じ込めを事前検証できない場合はGroup全Actor／ShapeをPhysics Sceneからまとめて除外し、完全Snapshotがあれば表示をそのGroup姿勢へ残し、なければ全該当表示を非表示にする。部分Kinematic化、個別Actorだけの復帰、旧Groupへの再合流を行わない。

- `ProvisionalFaultFrozen`では共用Geometryの背景処理だけを継続できるが、新しいProvisional分裂、Final Collider handoff、Dynamicへの自動復帰、切断Impulseを禁止する。到着済みまたは後着の物理成果物はCommitせず回収し、Cooked Geometry LeaseはFrozen Actor／ShapeをSceneから除去する最終破棄まで維持する。Primary Faultは`ProvisionalRuntimeFaultReason`、封じ込め結果は独立した`ProvisionalFaultContainmentDisposition`として確定し、原因を結果で上書きしない。このSnapshotは物理安全のFail-closed専用であり、表示―物理誤差の蓄積、補間、すり合わせには使用しない。


- 固定を設定する入力には、必要な1個以上のfiniteな点FixedSupportAnchorと、その初期Logical Convex Cell、Fragment Physics Frame内local位置を明示する。初期Cellとlocal位置は不変とし、現在所属は各Cellが保持するAnchor集合で表す。Anchor内に単一のmutableな現在Cellを持たせず、支持を設定しない入力には点Anchorを要求しない。

- 各切断では入力Cellが現在持つAnchorだけを、受付Snapshot、採用面、同じframeと既存のfiniteかつ非負なanchorEpsilon=eで分類する。`s = dot(planeNormal, anchorPosition) + planeDistance`がs>eなら正側、s<-eなら負側、-e<=s<=eなら両側へ継承する。無効値をOnPlaneへ分類しない。非交差Cellは対応する子へ継承し、OnPlaneでは同じAnchor値を有効な各子集合へ1回ずつ格納する。

- 同Sideの別Convex、旧Shapeを共有するSibling、反対側へ付属する表示GeometryへAnchorを複写しない。所有者単位で継承集合の有無を集約すればよい。内接削減、再cook、Collider頂点や祖先全一覧からAnchorを再生成・再発見せず、最近傍検索・包含探索・間接支持判定を行わない。

- Anchorを持つ所有者では離れたGeometry／Convexも全体を固定する。Phase 5.6で独立しAnchorなしになった所有者は動的となる。固定側のOffset、速度、切断Impulseは0とし、自由側だけを動かす。Provisional構築不能時は既存Pendingで旧物理を維持し、従来の自由側解析表示を使える。

- 点分類はcookを待たず入力と採用面から先行でき、少数Anchorは同期実行してよい。投機結果は既存Object／Anchor世代、採用面、入力Cell、frame、継承集合との一致を確認して採用する。未完了JobはWork依存で待ち、不正入力・世代不一致は既存検証と回収へ従う。支持付き対象は19.5.1の初期リベース対象外のままとする。

- 断面間の小さな見た目上のめり込み、見えている切断隙間に旧Colliderが残ることに加え、Provisional Shapeが表示Fragmentより張り出してめり込む前に外界へ衝突することを許容する。違和感とBroadphase拡大を限定するため、Provisional中の分離距離とConstraint法線移動には物体寸法と想定Impulseに基づく上限を設ける。Kerfは常に0であり、仮分離Offsetとは別パラメータとする。

- 後続の斬撃Hitと幾何切断はProvisional／旧Colliderではなく、公開済みの現在Logical Fragmentと共用Geometryを参照する。先行`LogicalCutOperation`が未公開の親とその仮表示領域には4.2の受付制限を適用する。Operation公開後は先行cook、Geometry Commit、Physics完了を待たず公開済みの子Logical Fragmentを再切断し、旧世代成果物はGeneration Rejectする。新世代Provisional構築は現ActorのPose／速度と共有Shape参照を基底に原子的に置換し、Actor、Shape Instance、Constraintの固定容量を超えた場合は新しい子だけを部分公開せず現物理Groupを維持する。

- Convex生成と`Physics.BakeMesh`をバックグラウンドで完了させ、成果物と世代が有効なら各Provisional ActorのCollider、mass、center of mass、inertiaをFinal値へ物理ステップ境界で原子的に置換する。単一Group Fallbackの場合だけ、このCommit時に複数Rigidbodyへ初分裂する。Bakeの遅延や失敗は即時表示または有効なProvisional運動を巻き戻す理由にしない。

- Collider差し替えとRigidbody分裂は物理ステップ境界で行う。

- Pending／Provisionalが予算時間を超えた場合はTraceへ`PhysicsSplitTimeout`を記録してcookをConfirmedPhysics優先度へ引き上げ、負荷待ちである限り現時点で有効な単一GroupまたはProvisional Actorを維持して処理を待たせる。Final Convex、質量または支持を成立させられないと確定した場合は、簡易Proxy、Compound Primitive、Geometry消去へ分岐せず、利用可能な既存物理表現を正式採用して終端できる。Provisional状態を短命とみなすProfile期限、Actor／Shape／Constraint上限、異常速度上限はT-091後に校正し、期限超過だけを理由にPose／速度を巻き戻さない。

### 7.2 Convex切断と運動継承

- 凸多面体を切断平面でクリップする。結果の正負側も凸となる。Runtimeで採用する1 Convex当たりの頂点上限を`L = 128`とし、入力登録と最終切断出力へ同じ値を適用する。Compound全体の合計上限ではない。入力は上限以下の有効なConvexとし、上限超過入力を登録時の自動削減で救済しない。10.2.2の早期PhysicsCookInput Role Gateとは独立したRuntime規約であり、既存Assetの一括再生成は要求しない。

- Physics ProxyのwatertightなConvex B-repをNative形式で保持し、頂点の正負分類、各面のPolygon clipping、交点／切断面Polygon生成、重複頂点統合、上限超過時の内接削減、凸性・閉性検証、体積・重心・慣性計算をJob＋Burstで行う。一般凸包の再計算は原則行わない。

- 出力数が不定なため、`ConvexCountJob -> Native領域確保 -> ConvexWriteJob -> ValidationJob`を基本Pipelineとする。内接削減は既存Writeの出力生成内で、Validation・質量特性計算・cookより前に完了する。Count側は通常clipの中間出力と局所置換に必要な補面の作業容量を確保し、最終上限Lを中間領域の上限に流用しない。初期の次数3方式では削除1回当たり最大1枚の補面を見込む。多数破片は同種段階をBatch化し、1破片ごとの極小Job乱発を避ける。削減専用のScheduler・Worker・必須追加Jobは作らない。

- 交差するConvexだけを切り、片側に完全にあるColliderはそのまま該当破片へ移す。

- 正負それぞれの出力Convexについて、通常clip・交点共有・重複統合後に頂点数がLを超えた場合だけ内接削減する。上限内では候補生成も削除も行わない。削除対象は今回のclipで生成した交点のうち、閉性・凸性・非退化性を保つ局所置換ができる頂点に限る。入力から継承した頂点は過去の切断で生成されたものも含め削除・移動せず、残す頂点の座標も変えない。重複統合元に継承頂点（OnPlane頂点を含む）が1つでもあれば継承扱いとし、代表座標には継承座標を使って削減対象外とする。統合元がすべて今回生成した交点の場合だけ削減候補にできる。「今回生成」の区別は切断中の作業情報だけで持つ。

- Phase 4の初期実装方針は次数3＋ΔV順の局所削除とする。現在のPolygon B-repで異なる隣接頂点がちょうど3個の新規頂点を候補とし、Cook三角形化の内部対角線は次数へ数えない。候補vの現在の隣接頂点をa、b、cとして、削る四面体の体積`ΔV(v) = abs((a-v) · ((b-v) × (c-v))) / 6`が小さい順に1頂点ずつ削除し、同値は既存local頂点順で決める。順位付けには分子だけを使ってよく、精度・epsilonは既存Convex幾何処理に合わせる。局所置換はvをincident Polygonから除いて境界をつなぎ直し、a、b、cを結ぶ外向き三角形で角を切り落とし、不要な面・辺参照を整理する。側面と補面を同時に更新し、頂点や断面だけを省略しない。

- 初期方式では削除ごとに変更近傍の次数・候補資格・評価値を更新し、次数が4以上になった点を候補から外す。任意の3隣接点や古い評価値で四面体式を使い続けない。次数3という候補条件とΔVによる削除順は唯一の製品合格方式には固定しない。同じ実装・入力条件では決定的に処理し、各削除で頂点数を減らしてL以下または候補不足で終了する。別順探索・巻戻し・全組合せ最適化は行わず、全入力で上限到達する保証は置かない。候補選択構造は実装に任せ、削除ごとの全Hull再検証や全候補の形状複製を要求しない。一般次数の削除、中点融合、一般凸包再構築や複数方式の選択基盤を今回の実装範囲へ追加しない。

- 切断前ConvexをP、採用面の片側半空間をH、通常clip結果をC、削減結果をQとして、幾何学的に`Q ⊆ C = P ∩ H ⊆ P`を保つ。単なる体積減少を包含の代わりにせず、Cの頂点の一部を残す閉凸境界として角を内側へ削る。数値誤差は既存Convex検証とFinalContainmentEpsilonに従い、新しい誤差Profileや完全証明器を作らない。最終出力は既存Validationへ通し、候補不足で上限超過が残る場合や既存の形状・質量・cook条件を成立させられない場合は7.3／7.6の既存物理正式採用と回収へ終端する。新しいFallback形状・再試行段階・復旧状態を作らず、有効な縮小結果は通常成功とする。

- 採用した削減済みB-repをCollider用Mesh、次回切断、Fast Simulationの同形状再cookの共通基底とし、削減前の大きい形状へ戻さない。自前B-repの内接性をcook後の実形状・Solver挙動すべての証明とはせず、既存のcook・handoff確認を使う。専用のcook結果抽出基盤は追加しない。点Anchorは既存Cell系譜を継承し、削除頂点から再構築しない。

- 質量特性のRuntime正本は共用Cut GeometryやStrict Solid Cut Meshではなく、切断対象のPhysics Convex B-repとする。表示Triangle全体の体積積分、Convex同士のBoolean Union、重複領域の厳密な控除は行わない。

- 切断前の各Physics Convexはbinary64でfiniteかつ0以上の`PhysicsConvexMassWeight`を持つ。同一FragmentGroupのConvexを`LogicalConvexFragmentLocalId`昇順へ並べ、IEEE 754 binary64の左畳みで`weightSum = (((0 + w0) + w1) + ...)`を求める。加算の再関連付け、並列Reduction、FMAによる式変更をCommit用結果では禁止し、各入力と各中間和がfinite、最終`weightSum > 0`、親Rigidbody質量がfiniteかつ正であることを必須とする。各Convexの配分質量は`assignedMass_i = parentMass * (weight_i / weightSum)`とする。Weight 0のConvexは衝突形状として保持できるが質量・慣性項へ寄与せず、全Weight 0、非有限、加算overflowではFinal分裂Commitを禁止して現有効物理状態を維持する。

- Provisional Actorの一時質量は、Cap Bounds等で既に必要な現在Source Actorの保守的OBBと今回のCut Planeだけを使う固定長切断から求める。OBB正負側のfiniteかつ非負な近似体積を`V+`／`V-`とし、両側にDirect Childが存在して`V+ + V-`がfiniteかつ正なら`M+ = budgetMass * (V+ / (V+ + V-))`、`M- = budgetMass - M+`の固定順でSide Budgetを作る。OBB体積が全0、非finite、または演算不能なら存在する正負Sideへ等分し、片側だけなら全量をその側へ残す。同一Sideには最大1物理子だけを作り、そのSide Budgetを割り当てる。蓄積全Cut PlaneでOBBを再clipせず、連続切断は現在ChildのCanonical Mass Budgetと現在OBBへこの一段処理を繰り返す。正でないChild mass、非finite、underflowで有効質量を作れない場合はProvisional分裂せず単一Group Fallbackへ送る。

- Provisional center of massはclip済みOBBの近似重心、inertiaはその保守的OBB／AABB箱慣性を割当質量で求め、近似が非finiteなら直前Actor inertiaを`provisionalMass / sourceMass`でscaleする。これらは短命なSolver用近似であり、Gameplay上の質量正本または次世代の`PhysicsConvexMassWeight`入力に使用しない。別途immutableな`CanonicalMassBudget`として切断直前の正規親質量とWeight系譜を保持し、Final Commitは必ずそこから本節の正規計算を行う。連続Provisional切断では現在Childへ割り当てたCanonical Mass Budgetをさらに分割し、一時Actor massから正本を作り直さない。

- Compound Convex同士が重なっていても生のConvex体積を単純加算して親質量を決めず、重複領域を二重計上しない。Weightは接触Geometryの体積そのものではなく、親の質量を保存しながら各Convexへ割り当てる物理近似Metadataである。

- 切断後はRigidbodyの動的／固定予定にかかわらず、各物理Commit対象Fragmentが所有するConvexだけを同じLocal ID順とbinary64左畳みで再集計し、`fragmentWeightSum`がfiniteかつ正であることを原子的Commitの必須条件とする。Weight 0のConvexを複数保持できるのは、同じFragment内に正のWeightを持つConvexが1個以上ある場合だけである。

- `fragmentWeightSum == 0`の子には質量0のRigidbody／任意の最小質量を生成せず、共用Geometryを消去しない。Weight不成立を片側空へ読み替えず、単一FragmentGroupまたは有効なProvisional Actor集合を正式採用してGeometryをその現在構成へ追従させる。部分的なFinal Rigidbody／Collider Commitや実装固有の質量再配分を行わない。

- 非交差ConvexのWeightは所属する子Fragmentへそのまま継承する。交差Convexは、そのConvexに割り当て済みのWeightだけを正負の採用出力Convexの有効体積比で分け、内接削減した側は削減後の体積を使う。体積比は`positiveVolume / (positiveVolume + negativeVolume)`とその補数を、正側、負側の固定順binary64加算から求め、両体積がfiniteかつProfileの`epsVolume`より大きいことを要求する。複数世代の切断でも子孫Weightの合計を親Weightと一致させ、最終的な全物理Fragmentの質量合計を切断直前Rigidbodyの質量と一致させる。非有限体積、体積和0、演算overflow、許容誤差外の質量不一致では正確経路をCommitしない。

- 出力の片側体積が`epsVolume`以下なら、その側へ質量0／任意最小質量のRigidbodyを作らず、共用Geometryも消去しない。正当なWeight配分を成立させられない場合は現有効物理状態を正式採用し、極小体積除算や恣意的な質量移送を行わない。

- 各出力Convexは内接削減後の採用形状からbinary64で`convexVolume`、局所重心、密度1の局所慣性`I_unitDensity`を計算する。正のWeightを持つConvexでは`convexVolume > epsVolume`を必須とし、`densityScale = assignedMass / convexVolume`、`I_assigned = I_unitDensity * densityScale`で割当質量へ変換する。`I_unitDensity * assignedMass`とはしない。1つの物理Fragmentを構成する全ConvexをLocal ID順の質量加重平均と平行軸の定理で合成して`centerOfMass`と慣性テンソルを得る。重なったConvexの重心・慣性もWeight付きCompound近似として受理し、厳密なUnion Solidの質量特性とはみなさない。

- 専用Physics Convexを持たない表示部分には独立質量を作らず、7.6に従って既存の物理所有者へ所属させる。表示部分の数を理由に質量を増減せず、極小Rigidbodyや質量移送を発生させない。

- Final質量特性の品質低下順は、`Convex多面体の正確な局所積分 -> ConvexごとのOBB箱慣性のWeight付き合成 -> Fragment全体OBB／AABB箱慣性`とする。Weight和0／非finite、親質量不正はGeometry近似では修復せず、現有効な単一FragmentGroupまたはProvisional Actor集合を維持する。正のWeightを持つConvexの体積不正または局所慣性不正だけをOBB以下へFallbackできる。下位経路でも同じassignedMass、親質量保存、finite、正の主慣性、決定的な軸規約を必須とし、同期Render Mesh積分やStrict Solid生成へFallbackしない。どの段階も成立しなければ現物理を正式採用してTraceし、受付済み操作を不要な再試行待ちへ残さない。

- Parent ActorからProvisional Actorを初めて作る時だけ、速度継承の正本点を`FragmentRenderAnchor`とする。Source ActorのCOM線速度から`v_anchor = v_sourceCOM + omega_source x (anchor - COM_source)`を求め、`v_provisionalCOM = v_anchor + omega_source x (COM_provisional - anchor)`、`omega_provisional = omega_source`を設定し、切断命中時の表示Fragment poseとAnchor点速度を連続させる。単一Group FallbackからFinalへ初分裂する場合も同じ初回分裂式を使用する。質量変更前後の運動量、角運動量、運動エネルギー保存は要求しない。

- ProvisionalからFinal Colliderへのhandoffでは物理Actorを正本とし、ActorのWorld pose、COM線速度、角速度をそのまま維持して、Final Shape、center of mass、inertiaだけを同一Actorへ置換する。Render Anchorを維持するためのActor pose補正や、新COMに合わせた線速度変換を行わない。Final Geometryは各Source Provisional Shapeの切断結果として同じFragment Physics Frameに保持し、由来Convexのhalf-space内に`FinalContainmentEpsilon`付きで収まることをCommit前に検証する。証明不能、許容外への張り出し、local frame不一致ではFinal Shapeを公開せず、利用可能な既存の物理表現を正式採用して終端する。19.5.1の採用Local Plane由来のFinalでもこの包含／local frame検証を省略せず、命中時Snapshotまたは予測Pose／速度へActorを戻さない。これによりFinal Collider自体を瞬間移動させて外界へ再penetrationさせる経路を作らない。表示GeometryはActorへ従属し、local origin／frame差によりFinal Commit時に瞬間的な位置・姿勢差が出ても許容する。分離ImpulseはProvisional生成時に一度だけ加え、Final Commitで重ねて再適用しない。単一GroupからFinalへ初分裂する場合だけCommit時に小さな分離Impulseを加える。Final Shape交換直後のSibling pairは既存の一時衝突抑止を使用できるが、外界とのGhost Contact履歴を理由にpose／velocityを巻き戻さない。

- Final handoffはSource Logical Convex CellのAnchor local位置と所属系譜を同じ子Cellへ引き継ぎ、Collider頂点・Shape共有・再cookからAnchorを再構築しない。物理固定は採用側が継承したAnchorの有無で決め、固定側のOffset／Impulseは0とする。

- 表示用MeshとCollider用Meshを分離し、Collider cooking用形状は低頂点・閉形状に保つ。

内接削減による次の品質変化は、後から形状を修復する前提の一時状態ではなく正式な近似結果として人間承認済みとする。

| 許容する結果 | 追加しない要求 |
| --- | --- |
| Colliderが表示より小さくなり、角・断面付近の接触取りこぼし、表示のめり込み、接触消失による落下・傾き・周辺運動の変化が生じる | 表示の完全被覆、接触中の削減禁止、接触維持用の拡張・位置補正 |
| 正負Collider断面の不一致・物理的な隙間、再切断での累積縮小、局所的な大きい欠けが生じる | 体積損失率・最大表面距離・累積誤差の品質上限、誤差履歴、後追い復元。表示のKerfは従来どおり0 |
| 表示だけの張り出しへの斬撃が7.6の片側空No-opとなり切れない | No-op専用Hull、表示Geometryでの二次受付判定、失われた当たり範囲の再生成 |
| 子への質量配分・重心・慣性が削減前の正確な切断結果と異なる | 削減前の質量特性を別正本とする補正。親質量保存は維持する |

初期方式の小さいΔVの優先は局所順位であり、見た目・接触・最終体積損失の最適性を保証しない。本節の不変条件を満たす削除順の変更により、採用形状・体積損失・接触・質量配分が初期方式と異なることを人間判断で許容する。同じ実装・入力条件での決定性は維持するが、実装変更をまたぐ同一形状は保証しない。保証は本上限対策で形状を外へ膨らませないことであり、あらゆる運動変化やpenetrationの完全防止ではない。表示Geometryの簡略化・削除、不正な閉性・凸性、非finite、正体積不成立、世代不一致の公開を許容しない。Actor pose／物理frame、Final包含、支持・資源寿命の既存契約は維持する。

7.9の追加分割は本節のConvex Kernel、Weight・質量、初回物理分裂の速度継承を再利用するが、命中・Pending・Provisionalを経由せず、攻撃／分離ImpulseとOffsetを加えない。本節の通常切断用の分離Impulse規則を任意分割へ適用しない。

#### 7.2.1 通常切断の正負二集合と非Union

一つの論理切断対象の共用Geometryと採用Convex集合へ同じ採用面を適用し、正負の面集合とConvex集合を作る。各側最大1の論理子・物理所有者へまとめ、正常な確定物理出力は合計1または2とする。この1／2はCompoundのConvex数、閉Shell・Contour・Cap数の上限ではなく、既存の容量条件は維持する。複数世代の系譜やScene全体のActor数を2個に制限しない。同Sideの離れた島を連結成分列挙で物体化せず、Geometry／Convexを離れたまま同じ所有者へ所属させる。全GeometryのHull、Union、隙間を埋める形状やJointを作らない。

LogicalFragmentは連結成分ではなく正負集合と再切断範囲を表す。共有Unity Rigidbodyを理由に指定された論理対象以外の全Siblingへ後続切断を広げず、祖先全体を切り直さない。Operation公開前の受付制限と、公開後のCPU子Geometryによる再切断は4.2に従う。

同Sideの島は相対姿勢・運動を共有し、一部への接触や外力が離れた部分へ作用すること、Anchorと同じ所有者に属する部品の空中浮遊、疎な集合のBounds拡大と同じ論理対象内の離れた部分への同一平面切断を許容する。部品間の接着意図は保持せず、後の追加分割で独立してよい。島ごとの独立化は7.9の任意処理だけとし、分割不能・未検出・未実装のままでも通常切断は完成する。遅れて分離した結果を最初から独立していた場合の軌道へ補償しない。Actor数の削減に比例してShape・Broadphase・Solver費用が減る保証はなく、効果は既存Profilerで測定する。

幾何切断に必要なRenderCutTopologyMap、Original Edge交点共有、Half-edge等の局所隣接、Contour接続、閉鎖・manifold・windingの継承は6章に従う。別Topologyを位置一致でweldせず、自己交差・非Unionの既存許容を維持する。

- 正負の別Rigidbody間でCollider overlapが大Impulseを生じる場合は、同一Cut Operation由来Sibling間だけ既存の一時衝突抑止を使用できる。外界接触を維持し、既存の相対分離閾値・Timeoutで再有効化する。安全に戻せなければ抑止を維持してTraceし、物理終端を理由に必要な抑止を強制解除しない。

- 検証済み左右ConvexをWritable MeshDataへ出力し、Main ThreadでUnity Meshへ適用する。そのMesh ID列をIJobParallelForへ渡し、Physics.BakeMesh(meshId, true, cookingOptions)を背景実行する。同じMeshを複数Jobから同時にBakeしない。完了後にSlashId、ObjectGeneration、入力Physics世代、Cooking Profileを照合し、安全な物理境界で有効な成果物だけ適用する。Schedule済みの古い成果物は完了後に回収する。7.9の非命中処理は同じKernelを使うが架空CutOperationを作らない。

#### 7.2.2 建物由来子のWorld D6と製品Jointスコープ

建物由来の動的な1→2物理分裂子へ、Worldに直接接続する`BuildingWorldD6Constraint`を一つ生成する。水平移動・回転の制限はbest-effortとし、落下・横転・完全倒壊を許容する。正負二集合、点Anchorによる固定判定、ProvisionalSeparationConstraint、Sibling衝突抑止、一般物理Fault処理とPlayer非接触は維持する。親・Sibling・建物Root・Ground用Rigidbodyを接続先にせず、Tree／Graph、到達性、構造解析を導入しない。

**Metadataと更新境界**

建物由来の判定と分裂の深さのために、各物理所有者へ継承可能な正本Metadataとして次の二値だけを追加する。実装名は固定しない。

| 値 | 初期値・意味 |
| --- | --- |
| `IsBuildingDerived` | 製品Asset Recipeで明示した建物の初期所有者はtrue、その他はfalse。通常切断・追加分割の子孫へ継承する |
| `BuildingSplitDepth` | 初期値0の非負整数。建物由来の系譜で公開した1→2物理分裂の深さを表し、表示切断数は数えない。非建物の系譜は0を維持する |

建物由来をRuntimeのBounds、名前、Material、Slab数等から推測しない。建物Metadataの製品生成はPhase 5.5へ置き、Phase 4.3では手書きSynthetic入力を使う。

通常切断と7.9の追加分割は、`IsBuildingDerived=true`の場合だけ、Provisionalを含む実際の1→2物理所有者の原子的公開時に一度だけ、両子のDepthを親から1段進める。非建物の系譜は`IsBuildingDerived=false`とDepth 0を維持する。Depthは非負とし、整数型とwrapを避ける飽和方法は実装詳細へ置く。再採番・専用枯渇状態は設けない。正常な1物体結果、受付前No-op、Stable Unsplit、未公開候補の不成立・世代失効では進めない。Provisionalを2 Actorで公開した後にFinalが片側空となる場合や、既存Provisionalを正式採用する場合も、公開済みDepthを再加算・巻戻ししない。Final handoff、COM／inertia更新、Fast Simulation Upgrade、単なるActor内部更新は新しい分裂に数えず、存続するActorのD6基準姿勢・Limitをリセットしない。

**適用と設定**

D6生成対象は、`IsBuildingDerived=true`かつ点Anchorなしの動的所有者で、成功した1→2物理分裂の新しい子だけとする。点Anchorが一つでもある子はStaticまたはKinematicとし、建物D6を付けない。初期建物そのものへのD6追加は必須範囲に含めない。

基準位置・回転は子Actorを公開する物理境界でのWorld poseとし、生成直後の相対並進・相対回転が0となるようActor側／World側AnchorとJoint frameを構成する。予測姿勢・命中Snapshot・祖先姿勢へ巻き戻さない。World Y方向の並進はFree、水平2軸の並進と全3軸の回転はLimitedとし、水平2軸には同一距離Limit、全回転軸には生成時姿勢に対する同一の対称角度Limitを使う。

設定値は初回分裂子の水平距離`L1`、角度`A1`、共通減衰率`r`（`0 < r < 1`）の3値とし、Depth `d >= 1`に対して`L(d) = L1 * r^(d-1)`、`A(d) = A1 * r^(d-1)`を適用する。算出Limitは有限・非負とし、underflowの処理は実装詳細へ置く。正のminimum、Locked切替、深度別table、距離と角度で別の減衰率を追加しない。Drive、Spring／Damperによる復元、Projection／Transform Snap、Gameplay用Break Force／Torque、形状・質量・接触による動的Limit変更を使わない。製品値はO-048で決め、実際の総変位・総回転の上限を式から保証しない。

**生成・維持・退役**

| Constraint | 接続先 | 寿命・目的 |
| --- | --- | --- |
| `ProvisionalSeparationConstraint` | 正負Sibling | Provisional期間の旧Convex共有中の再侵入抑止 |
| `BuildingWorldD6Constraint` | World | 当該Actor寿命にわたる水平移動・回転のbest-effort制限 |

両者は統合しない。通常切断ではProvisional子、Provisionalを経由しなければ初めて1→2公開するFinal子の公開前構築へ必要D6を含める。成功したD6はFinal handoff／Upgradeで作り直さない。再分裂では親D6を付け替え・複製せず、新しい動的子ごとに現在World poseを基準として生成し、親D6は親Actorと同じ既存物理Step・参照寿命後に一度だけ退役する。1物体結果の不要Actorや物理GC対象も同じ所有資源寿命に従う。専用Lease、Constraint世代、親子参照表を追加しない。

必要D6は既存Constraint容量とActor／Shapeの原子的構築へ含め、D6なしの対象子を成功扱い・部分公開しない。設定・生成・公開を成立させられない場合は既存のConstraint容量／生成・原子的構築不能規則で現在有効な物理を維持する。一時的な実行枠不足は既存Pending、要求構成を成立させられない場合は既存物理の正式採用へ進む。7.9の任意分割では候補全体を不採用とし、親Actor・Depth・D6を変更しない。専用状態、Fallback Reason、Retry Queue、代替Constraintを設けない。

**製品入力の限定**

切断対象として登録する所有者は、切断システム所有の`ProvisionalSeparationConstraint`と`BuildingWorldD6Constraint`を除き、別RigidbodyまたはWorldとのJoint／Constraint関係を持たない。他物体のJoint接続先も対象外とする。扉、吊り看板、Chain／Spring付きプロップ、別Gameplay Systemが拘束する物体を初期製品の切断対象に含めない。

一般外部JointはAsset／Scene登録時の切断対象契約で除外できれば十分とし、毎Frameの探索、接続先からの逆参照、子への継承・付け替え・複製、GC保護、分割見送り、専用Trace・理由を要求しない。登録後に別Systemが外部Jointを追加することは契約外とする。未来予測は19.3～19.5の既知Constraint判定を使用する。将来のシステム所有Constraint追加は個別判断とし、汎用Constraint Provider／Plugin層を先行実装しない。

**人間承認済みの許容とPhase境界**

倒壊抑制が効かないこと、垂直方向の無制限移動、建物Root／Platformへ追従しないこと、独立したWorld基準と世代ごとの移動・回転の累積を許容する。小さいLimitでのjitter・drift・Solver未収束、接触・分離Impulse・Sibling抑止との競合、不自然な隙間・姿勢、penetration・接触Impulse・表示不連続もD6で防ぐ保証を置かない。動的建物子ごとのJoint数・Solver CPU・メモリ・生成退役費用の増加、一般Joint付き製品の除外、建物D6付き物体の初期未来予測対象外を許容する。拘束効果の監視・救済や性能SLAを追加せず、非finite・異常速度等の一般Fault、世代Commit、メモリ・資源寿命は維持する。

Runtime本体と既知Constraintの識別境界は、ロードマップ上の独立Phase 4.3で通常切断へ接続する。Phase 4の汎用物理、4.1のGeometry／Cook Baseline、4.2のPlayer非接触を再オープンせず、4.5より前にT-094で完了する。製品Recipeは5.5、任意分割・GCへの統合は5.6／5.7で確認し、これらや未来予測本体を4.3の完了条件へ前倒ししない。

#### 7.2.3 プレイヤー非接触Locomotion

初期仕様ではPlayer Body／Hand用Layerとプロップ／破片のPhysics Layer間の接触を無効化し、プレイヤーは物体を押さず、物体もプレイヤーを押しやらない。床移動と人工移動による代表的な壁への新規侵入はRigidbody接触ではなく、建物壁板、固定大型プロップ、レベル境界から作る低複雑度`PlayerLocomotionOccupancy`に対する次姿勢Queryで抑制する。これはCameraと全Render Geometryの厳密な非交差を保証する仕組みではない。斬撃は従来どおりBlade／SlashFrontの論理Sweepで成立するため、非接触化しても主要Interactionを失わない。

- Occupancyは表示Triangleや切断前の旧Colliderをそのまま使用せず、現在のStructural Slab OBB／少数Primitiveとレベル固定境界から作る。現在の物理所有者の確定物理姿勢へFixed Step後に追従し、Pending中は保守的な旧領域を維持する。切断開口を移動可能域へ反映する時期はStable Geometryと確定物理姿勢が揃った後とし、即時Rendererだけを根拠に通行を許可しない。

- スティック移動や人工移動では、Player Rootと予測HMD Capsuleの次姿勢がモデル化済み禁止Occupancyへ新規侵入する操作を止める。実空間の6DoF頭部移動を仮想Camera Clampや物理Impulseで押し戻さず、簡易Volumeとの重なりが検出できた場合だけNear-Wall Fade／視界マスクをbest-effortな視界保護として適用する。Fadeが全てのGeometry被りを隠すことは保証しない。

- HMD視点の近似判定は、PlayerLocomotionOccupancyに登録された大型固定／構造プロップとレベル境界のOBB、Box、Capsule等に対する小さなHead Sphereまたは予測HMD CapsuleのOverlapまでとする。小型プロップ、装飾、一般の非接触Fragment、実Render Triangle、複雑な切断面を網羅しない。Meshのinside／outside、Ray parity、generalized winding、切断後Mesh全体への包含Queryを行わず、未登録物体や非干渉物体がHMD視点へ被ること、Cameraがそれらの内部へ入ること、Near Planeで内部面が見えることを許容する。

- 即時切断物体へHMDが入った場合は5.2のTemporary Stencil例外を適用する。スクリーンスペースの一部だけに仮Capが現れる、Capが欠ける、内部面が見える、左右眼で差が出る状態を許容し、Player／Cameraまたは物体を強制移動しない。Camera overlapを切断Topology、物理、Geometry Commitの失敗Reasonにせず、切断Jobの取消・再発行や同期Fallbackも行わない。

- `PlayerLocomotionPolicy`は`NewEntryReject=1`、`PushOut=2`、`ExitOnly=3`の固定候補とし、0を未設定、未知値をRejectする。すでに禁止領域へ入ったPlayerを外へ押し出す`PushOut`と、「侵入深度を増やす移動だけ拒否し、減らす移動は許可する」`ExitOnly`はプレイテスト後に選ぶ。それまでは接触Impulseを導入せず、`NewEntryReject`と視界保護をPoC正本とする。

- PoCの`NewEntryReject`中に、静止中のPlayerへ現在の物理所有者側のOccupancy更新が重なった場合は`ForcedOccupancyOverlap`へ入る。物理所有者のCommitをPlayer位置だけを理由にRejectまたは巻き戻さず、PlayerをImpulseや強制位置補正で押し出さない。Episode開始時にはLocomotion Upに直交する2次元`AllowedLocomotionPlane`と`ExitSearchMaxHorizontalExpansion = 2.0 m`という寸法上限だけを固定し、world-space `ExitSearchBounds`の位置は固定しない。各Fixed Stepの候補生成前に、現在Player Capsuleと、人工移動要求の平面内長さ`s`を全方向へ適用し得る保守的`CandidateSweepEnvelope`とのunionを現在Player位置基準で一度だけ求める。全Line Search候補は長さ`<= s`なのでこのEnvelopeへ含まれなければならない。必要な水平拡張がProfile上限を超える、またはBoundsが非finiteならVolume Queryや部分候補評価を行わず`OccupancyExitBlocked(SearchBoundsExceeded)`へ移る。

- 上限内なら、そのFixed Stepの`ExitSearchBounds`を上記unionへ確定し、このBoundsと保守的Boundsが交差する全Occupancy Primitiveを先に収集して全候補共通のVolume集合とする。候補ごとにBoundsやVolume集合を変えず、現在／候補姿勢で非Overlapなら深度0として評価する。これにより通常LocomotionでEpisode開始地点から任意距離移動済みでも、現在Playerへ侵入しているVolumeは収集対象になる。単一の最深Volumeだけを正本にせず、垂直法線をAllowed Planeへ射影してzeroになる場合はその法線を退出候補に使用しない。PoC Profileの初期値は`MaxExitVolumeCount = 8`、`ExitLineSearchSteps = 4`、`MaxExitCandidateCount = 192`、`ExitBlockedFixedStepLimit = 15`、`MaxForcedOverlapFixedSteps = 180`とし、T-088後に校正する。

- Volume収集、法線、方向、展開済み候補、Depth Vector、MetricはProfile上限で初期化時に一度だけ確保する固定長Native／unmanaged作業領域へ格納し、Episode中のManaged allocation、Buffer成長、GCを禁止する。Broadphaseは`MaxExitVolumeCount + 1`件の固定長NonAlloc Bufferまたは同等のoverflow flag付きQueryを使い、上限超過を無制限列挙せず検出する。結果が`MaxExitVolumeCount`を超えた場合はID順の先頭だけを使わず、深度計算とPlayer移動を開始する前に`OccupancyExitBlocked(VolumeCapacityExceeded)`へ移る。非zeroな人工移動要求の平面内長さを`s`とする。有限候補方向は、平面へ射影した要求方向、各Overlap Volumeの解析的外向き法線の非zero射影、全有効法線の正規化和、全ての非zeroな法線2本の正規化和をこの順で生成し、Volume組はLocal ID辞書順、同じcanonical binary32方向は最初の1件だけ残す。各方向について固定`ExitLineSearchSteps`の`stepLength = s * 2^-k`、`k = 0..ExitLineSearchSteps-1`を評価する。中心一致等で法線が一意でないPrimitiveはlocal `+X, -X, +Y, -Y, +Z, -Z`順の軸をWorld／Allowed Planeへ射影し、最初の非zero方向だけを候補にする。要求量`s > 0`以外の最小進捗量を設けない。

- 方向生成前に、収集Volume数から`1 + V + 1 + V * (V - 1) / 2`のchecked整数上限を求め、`ExitLineSearchSteps`を掛けた展開候補上限が`MaxExitCandidateCount`以下であることを検証する。deduplicateによる減少を容量成立の前提にしない。checked overflowまたは上限超過では候補を途中まで生成・評価せず`OccupancyExitBlocked(CandidateCapacityExceeded)`へ移り、そのTickまでに作った部分候補の移動を適用しない。

- 同一Fixed Step Snapshot上の各候補について、収集済み全Volumeの非負binary64侵入深度を固定長領域へ求める。`ExitMetric = (MaxDepth, SumDepth, DepthByVolumeId[])`とし、`MaxDepth`は全値の最大、`SumDepth`はLocal ID昇順のbinary64左畳み、末尾VectorもLocal ID昇順とする。非Overlapは0、非finiteは候補Rejectとする。現在姿勢よりこのTupleがIEEE 754数値順で辞書式に厳密減少する候補だけを許可し、最小Metric、同値なら要求方向とのdotが最大、さらに同値なら候補生成順が早いものを適用する。このためPlayer自身の適用移動が同一Snapshot上で過去姿勢へ周期的に戻ることはない。適用後は次Fixed Stepの更新済みSnapshotで再評価する。

- `ForcedOccupancyOverlap`へ入ったFixed Stepを0として、候補の有無やMetric進捗にかかわらずEpisode経過をsaturating counterで数える。全深度が`occupancyExitEpsilon`以下になる前に`MaxForcedOverlapFixedSteps`へ到達した場合は`OccupancyExitBlocked(EpisodeTimeout)`へ移る。減少候補がないTickではPlayerを動かさず、物理Impulseや任意軸Fallbackで押し出さない。非zero入力が`ExitBlockedFixedStepLimit`回連続しても減少候補を得られない場合は`OccupancyExitBlocked(NoDecreasingCandidate)`へfail-closedする。Candidate SweepがBounds寸法上限を超える場合は前述の`SearchBoundsExceeded`を使用する。Blocked後は人工並進を停止してNear-Wall Fade／視界マスクを維持し、明示的なユーザー操作による最後の安全なLocomotion Poseへの復帰、Level Reset、またはOccupancy側が移動して減少候補が再出現した場合だけ再開する。全侵入深度がfiniteかつ`<= occupancyExitEpsilon`になれば重なりなしへSnapして通常の`NewEntryReject`へ復帰する。実空間HMD 6DoFは常にClampしない。これは`ExitOnly`を最終Policyとして採用したことを意味せず、移動Occupancy起因の安全Fallbackに限定する。

- Player接触を物理世界から除外することで、プレイヤーの身体接触による未来Physics結果の無効化を発生させない。Player位置と斬撃は依然として介入条件だが、Fragmentの投機物理Commit条件へPlayer接触Impulse履歴を追加しない。

### 7.3 Collider Cooking Profile

ランタイム生成するPhysics Proxyは、7.2の上限超過出力に対する内接削減を含む自前のConvexクリップと検証器でwatertight、面向き、凸性、退化三角形、重複頂点、極短辺、自己交差、頂点・面数上限を保証してからcookへ渡し、上限対策をCooker任せにしない。契約を満たしたMeshでは`EnableMeshCleaning`と`WeldColocatedVertices`を無効化する構成を有力候補とし、Unityへ重複作業をさせない。検証に失敗した入力を軽量ProfileのままBakeせず、利用可能な既存物理表現を正式採用して終端する。

初回の物理分裂には原則Fast Cookを使い、`PendingPhysicsSplit`を早く終了させる。物理分裂後、余剰CPU時間に同一形状をFast Simulationで再Bakeし、価値のある破片だけを低優先度で昇格させる。両Profileの単独比較に加え、この二段階運用が総コストを下げるか実測する。

| Profile | `MeshColliderCookingOptions`候補 | 目的 |
| --- | --- | --- |
| Fast Cook | `None` | 任意の追加工程を省き、Bake待ちキューと`PendingPhysicsSplit`滞留を短縮 |
| Fast Simulation | `CookForFasterSimulation` | 追加cookを許容し、完成後の破片衝突・Query負荷を低減 |

Fast Simulationへの昇格候補は、長寿命、プレイヤー近傍、接触／Query頻度が高い、今後も動く見込みがある破片を優先する。遠距離またはSleep済みの破片はFast Cookのまま残してよい。Upgrade同時実行数、待ちキュー、二重Meshの一時メモリへ上限を設ける。

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

計測は少なくとも、自前Convex clipping=`PolygonClip`、Descriptor／MeshData構築=`DescriptorBuild`／`MeshDataBuild`、Native境界転送=`NativeBoundaryTransfer`、`ApplyAndDisposeWritableMeshData`=`MeshApply`、Hull計算=`HullComputation`、PhysX内部形式生成=`PhysXFormatBuild`、Stream処理=`StreamSerialize`／`StreamLoad`、`Physics.BakeMesh`=`Bake`、Collider Commit=`Commit`へ分離する。Phase 0.25では4、16、64、128頂点の公開PhysicsCookInputと最大128頂点のLicensed補助入力を用いる。8、32、255頂点級等の拡大系列はPhase 4／4.1で別途生成・検証する。各段階の有効入力について単発／Batch、同時Slash、Fast Cook／Fast SimulationでP50／P95／P99、Throughput、Worker占有、Main Thread時間、一時／最終メモリ、失敗率、生成頂点／面、接触／Query品質を比較する。

Native PhysXが生成した`PxConvexMesh`またはCook済みBinaryをUnity `MeshCollider`へ注入する公開経路は前提にしない。大差が出ても、まずUnity経路のBatch化、Cooking Profile、入力簡略化、二段階Collider、Cacheで要件を満たせるか確認する。Native採用は、Unity経路のP99が実際にPending／90Hz予算を破り、差が継続的かつ大きく、Unity側で回避不能な工程にあり、Native成果物を実ゲームへ統合する別の小型Prototypeが成立した場合だけ再検討する。この場合はcook関数だけの交換ではなく、切断破片のQuery／接触／Scene同期を含む物理経路の部分置換として見積もる。

### 7.5 Geometry／Cook Microbenchmark

CPU側の共用VP Geometry切断、Convex切断、Temporary Physics Proxy生成、cookの各実装が個別に正しい結果を生成できた時点で、予算校正と追加最適化を行う前の初期製品実装Baselineを取得する。Job／Burst、Batch、Fast Cook／Fast Simulation等のPhase 3／4で採用済み基本経路はBaselineに含む。目的は最速値の宣伝ではなく、入力規模から単発完了時間、Jobキュー滞留、斬撃波到達までの完了率、同時Pending上限を見積もるための容量モデルを作ることである。Phase 0.25のT-070は固定ConvexによってUnity／Native cook経路の差と改善上限を早期に調べるProbeであり、T-076の前提ではない。Phase 4.1のT-076は製品の共用Geometry／Convex／Physics Proxy生成経路が完成した後に取得し、T-069の統合測定を補完するとともに、T-070の早期結果を製品入力分布と工程内訳から再解釈するBaselineとする。

`SharedMeshCut`は既存schema tokenとして維持し、CPU側の共用VP Geometry切断Kernelを表す。新しいTarget値の追加・改名は行わない。

計測単位は次の責務で分け、複数工程を一つの数値へ混ぜない。表示VPの出力・GPU更新・参照公開に関する詳細対応はPhase 3具体化時に決め、表示側にMeshData構築や厳密Count／Write分割を要求しない。表示切断のCountは実装した場合だけ測る任意工程とする。既存の物理MeshApply／cook測定は維持する。下表のMeshPublish／MeshApplyはUnity Meshの公開を測るもので、表示VP公開へ改名・流用しない。

| 対象 | 分離して測る工程 | 主な規模軸 |
| --- | --- | --- |
| 共用Geometry | Topology Vertex分類、Original Edge交点共有、Contour／Cap生成、変更部の共通契約検証、Metadata、VP出力生成 | 入力／出力Triangle数、交差Original Edge数、Contour数、Cap数、Fragment数、累積切断面数 |
| Physics Convex | Convex Count、Polygon clipping、切断面生成、内接削減を含むWrite、Validation、体積／重心／慣性、Collider用MeshData構築 | Convex数、各Convexの頂点／面数、交差Convex率、出力Convex数 |
| Temporary Physics Proxy | Bounds／切断面からの簡易ConvexまたはCompound Primitive生成 | Primitive数、Fragment数、入力Bounds／切断面数 |
| Cook／Commit | `Physics.BakeMesh`のFast Cook／Fast Simulation、Mesh公開、Collider Commit | Convex頂点数、Bake数、Batch Size、Profile、同時実行数 |

同じPure Native入力と出力Bufferを使い、共用Geometry／Convex／Temporary Physics Proxyの計算Kernelだけを同期実行する`Single-Thread Kernel`と、実際の`Schedule -> Worker実行 -> Complete`を使う`Job Batch`を分離する。前者は`µs/op`、入力／出力要素当たり時間、P50／P95／P99を記録し、Job Schedule、GC、Unity Object生成を含めない。Unity API境界を含む`Physics.BakeMesh`、Mesh公開、Collider CommitはPure Kernel値へ混ぜず、直列の単発LatencyとBatch時のEnd-to-End値として別記する。Job側は`cuts/s`、`input triangles/s`、`output triangles/s`、`convexes/s`、`cooks/s`、Job End-to-End latency、Schedule時間、Worker占有率、Main Thread Commit時間を記録する。単発Jobのレイテンシと十分なBatchを連続投入した定常Throughputを混同しない。

Phase 4.1の固定Datasetには、公開可能な合成Fixtureをcanonical正本として、共用Geometry 500／1,000／3,000／10,000／30,000 Triangle級、Convex 8／16／32／64／128／255頂点級、1／4／16／64 Convex、2 Fragment、および複数操作・Batchでの4／8 Fragment、中央切断／端切断／非交差、単一／複数断面、単純／複数Cap Loopを含める。Cap Loop等の閉形状既知正解にはSynthetic Watertight Test Fixtureを使う。この拡大規模系列はPhase 4／4.1で生成・検証し、Phase 0.2の単一Hull128頂点Cook入力だけから全系列が得られるとはしない。製品ConvexCutの入力は7.2のL以下とし、上限内出力と内接削減を要する出力の費用を既存Write／WholePipelineで測る。255頂点系列はCook単体比較として後段で独自に成立確認し、Runtime登録・切断の成功条件やPhase 0.2の255頂点Codec-only caseとは分離する。内接削減専用のBenchmark Stage／Schema、数値SLA、大規模matrixを追加しない。Temporary Physics Proxyは1／4／16 Primitive級を初期候補とする。Phase 0.2で自動選抜したSynty／Poly Pro Universe等に由来する`LicensedRepresentative` Render／Convex Fixtureも非公開の補助Suiteとして測定し、RenderはOriginal、Boundary Fill、約100、500、1,000、2,000、5,000、10,000 Triangleを要求するDirect Variantに加え、カテゴリ別Recipe（Building DepthWall／SurfaceFill OFF／ON、Vehicle 1 cm Voxelと限定Post-Decimate等）を用途Binding単位で比較する。各要求値と実出力を分離し、Manifestの規模軸には実Triangle数を使う。合成Fixtureの代替や全Asset互換性の証拠にはせず、公開結果から入力GeometryやAsset対応を復元できるデータは保存しない。

Release Player相当、Burst有効、Jobs Debugger／Safety Checks無効を採用判断用の正本とし、Editor値は開発時の回帰検出専用とする。Cold start、初回JIT／Burst Compile、Allocator拡張は定常値と分け、Managed GC、Native一時メモリ、失敗率も記録する。結果の正しさを事前検証し、無効出力や早期Rejectを成功経路の高速値へ混ぜない。Temporary Physics ProxyはT-077の正しさ検証を通過した実装だけをT-076の性能比較へ含める。

性能測定の環境情報は既存の`TraceRunManifest`を拡張せず、別schemaの`GeometryBenchmarkRunManifest`へ保存する。1 Manifestは単一`DatasetCaseId`の固定入力に対する「単一`BenchmarkTarget`、単一`BenchmarkStage`、単一`ExecutionMode`、単一`CookingProfile`、単一`BenchmarkMetric`、単一`MeasurementUnit`を反復する1測定系列」だけを表す。T-070／T-076の1回のHarness実行は複数Manifestを生成し、全系列へ同じ`BenchmarkSuiteId`を付けて束ねる。各系列は異なる`BenchmarkRunId`を持ち、同じSuite内でRun IDと`Target + Stage + ExecutionMode + CookingProfile + Metric + Unit + DatasetId + DatasetContentSha256 + DatasetCaseId + BatchSize`の組を重複させない。別case、別工程、別指標は同じManifestへ混在させず、同じSuiteの別系列として保存する。

以下のSchema v1をGeometry Benchmarkの初回実装定義とする。設計履歴にだけ存在した旧`DisplayMeshCut`、`TemporaryLowPolyProxy`および廃止済み救済StageのReader、互換enum、変換器、旧新比較試験は作らない。

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
| `PrimitiveCount` | integer | 必須。Temporary Physics Proxy等のPrimitive数`0..2147483647`。非該当Targetでは0 |
| `CutPlaneCount` | integer | 必須。`0..2147483647`。切断を伴わないTargetでは0 |
| `BatchSize` | integer | 必須。`JobBatch`では`2..1000000`、それ以外のExecutionModeでは厳密に1 |
| `WarmupIterations` | integer | 必須。`0..1000000`。定常Baselineでは1以上、Cold測定だけ0を許可 |
| `MeasurementIterations` | integer | 必須。`1..1000000`。GeometryBenchmarkResult v1の最大Sample試行数と一致 |
| `CookingProfile` | string enum／null | Cook Targetでは`FastCook`／`FastSimulation`を必須とし、共用Geometry／Convex切断／Proxy／Commit等の非Cook Targetでは厳密に`null` |
| `BenchmarkTarget` | string enum | 必須。`SharedMeshCut`／`ConvexCut`／`TemporaryPhysicsProxy`／`UnityBakeMesh`／`NativePhysXComputeHull`／`NativePhysXCompleteTopology`／`NativePhysXDirectInsertion`／`MeshPublish`／`ColliderCommit` |
| `BenchmarkStage` | string enum | 必須。`WholePipeline`／`PlaneClassification`／`Count`／`Write`／`IntersectionMerge`／`TopologyIntersectionShare`／`ContourTrackBuild`／`ContourIntersectionTest`／`CapLoopBuild`／`CapTriangulation`／`CapArrangement`／`Metadata`／`PolygonClip`／`CutFaceBuild`／`Validation`／`MassProperties`／`DescriptorBuild`／`MeshDataBuild`／`ProxyGeneration`／`NativeBoundaryTransfer`／`MeshApply`／`HullComputation`／`PhysXFormatBuild`／`StreamSerialize`／`StreamLoad`／`DirectInsertion`／`Bake`／`Schedule`／`WorkerExecution`／`Complete`／`Commit` |
| `ExecutionMode` | string enum | 必須。`SingleThreadKernel`／`SerialApiLatency`／`JobSingle`／`JobBatch`／`MainThreadCommit` |
| `BenchmarkMetric` | string enum | 必須。`Latency`／`Throughput`／`InputRate`／`OutputRate`／`WorkerOccupancy`／`ManagedAllocation`／`NativeMemoryPeak`／`FailureRate`／`ScheduleCount` |
| `MeasurementUnit` | string enum | 必須。`Microseconds`／`MicrosecondsPerOperation`／`OperationsPerSecond`／`CutsPerSecond`／`InputTrianglesPerSecond`／`OutputTrianglesPerSecond`／`ConvexesPerSecond`／`CooksPerSecond`／`Percent`／`Bytes`／`Count`／`FailuresPerMillionOperations`から1つだけ選ぶ |
| `TraceRunManifestContentSha256` | string／null | Trace参照時は小文字64桁`[0-9a-f]{64}`、未参照時は厳密に`null` |

canonical JSONは全propertyを上表順序で常に出力し、UTF-8 BOMなし、余分な空白と末尾改行なし、不変Cultureの数値表現とする。nullable propertyも省略せず上表の条件で文字列またはJSON `null`を出力する。Cook Targetは`UnityBakeMesh`と3つの`NativePhysX*`、Native Targetは3つの`NativePhysX*`と定義する。CodecはTargetとStage、ExecutionMode、`NativePhysXVersion`、`CookingProfile`の組合せを検証し、`WholePipeline`以外のStageを無関係なTargetへ指定できないようにする。

`BenchmarkTarget × BenchmarkStage`の許可集合は次を正本とする。`Schedule`／`WorkerExecution`／`Complete`はJob実装を持つTargetだけで使用し、表にない組合せはCodecでRejectする。`WholePipeline`は対象のEnd-to-End系列であり、下位Stageの代用として工程別必須系列を省略してはならない。

| BenchmarkTarget | 許可するBenchmarkStage |
| --- | --- |
| `SharedMeshCut` | `WholePipeline`、`PlaneClassification`、`Count`、`Write`、`IntersectionMerge`、`TopologyIntersectionShare`、`ContourTrackBuild`、`ContourIntersectionTest`、`CapLoopBuild`、`CapTriangulation`、`CapArrangement`、`Metadata`、`Validation`、`Schedule`、`WorkerExecution`、`Complete` |
| `ConvexCut` | `WholePipeline`、`PlaneClassification`、`Count`、`PolygonClip`、`CutFaceBuild`、`Write`、`Validation`、`MassProperties`、`MeshDataBuild`、`Schedule`、`WorkerExecution`、`Complete` |
| `TemporaryPhysicsProxy` | `WholePipeline`、`ProxyGeneration`、`Validation`、`MeshDataBuild`、`Schedule`、`WorkerExecution`、`Complete` |
| `UnityBakeMesh` | `WholePipeline`、`DescriptorBuild`、`MeshDataBuild`、`MeshApply`、`NativeBoundaryTransfer`、`Bake`、`Schedule`、`WorkerExecution`、`Complete` |
| `NativePhysXComputeHull` | `WholePipeline`、`DescriptorBuild`、`NativeBoundaryTransfer`、`HullComputation`、`PhysXFormatBuild`、`StreamSerialize`、`StreamLoad` |
| `NativePhysXCompleteTopology` | `WholePipeline`、`DescriptorBuild`、`NativeBoundaryTransfer`、`PhysXFormatBuild`、`StreamSerialize`、`StreamLoad` |
| `NativePhysXDirectInsertion` | `WholePipeline`、`DescriptorBuild`、`NativeBoundaryTransfer`、`PhysXFormatBuild`、`DirectInsertion` |
| `MeshPublish` | `WholePipeline`、`MeshApply`、`Commit` |
| `ColliderCommit` | `WholePipeline`、`Commit` |

規模軸はDataset caseの固定説明変数であり、Samplesとは独立してManifestへ保存する。`SharedMeshCut`／`MeshPublish`はTriangle／Edge／Cap／Fragment軸、`ConvexCut`はConvex／Convex Vertex／Fragment／Cut Plane軸、`TemporaryPhysicsProxy`はFragment／Primitive／Cut Plane軸、Cook Target／`ColliderCommit`はConvex／Convex Vertex軸を使用する。使用しない軸は厳密に0とし、同じ`DatasetId + DatasetContentSha256 + DatasetCaseId`で軸値が異なるManifestを同一Suiteへ含めない。CodecとSuite LoaderはこのTarget別規模軸規則を検証する。

同一`BenchmarkSuiteId`内では、1つの`DatasetId`を厳密に1つの`DatasetContentSha256`へ対応させる。Suite Loaderは全Manifestを読む際に`DatasetId -> DatasetContentSha256`の写像を構築し、同じDatasetIdから異なるhashが1件でも現れたSuite全体を、Resultの読込や容量式tableへのjoin前にRejectする。異なるDataset版を比較する場合は別`BenchmarkSuiteId`で測定するか、版を表す別`DatasetId`を明示的に割り当てる。同一Suite内でhashだけを変えてLatency、Throughput、FailureRate等の系列を混在させることは禁止する。

`BenchmarkStage × ExecutionMode`の許可集合も固定し、Target×Stage表との積で最終的な許可組合せを決める。

| BenchmarkStage分類 | 許可するExecutionMode |
| --- | --- |
| `PlaneClassification`、`Count`、`Write`、`IntersectionMerge`、`TopologyIntersectionShare`、`ContourTrackBuild`、`ContourIntersectionTest`、`CapLoopBuild`、`CapTriangulation`、`CapArrangement`、`Metadata`、`PolygonClip`、`CutFaceBuild`、`Validation`、`MassProperties`、`MeshDataBuild`、`ProxyGeneration` | `SingleThreadKernel`、`JobSingle`、`JobBatch` |
| `DescriptorBuild` | `SingleThreadKernel`、`SerialApiLatency`、`JobSingle`、`JobBatch` |
| `NativeBoundaryTransfer` | `SerialApiLatency`、`JobSingle`、`JobBatch` |
| `HullComputation`、`PhysXFormatBuild`、`StreamSerialize`、`StreamLoad`、`DirectInsertion` | `SerialApiLatency` |
| `Bake` | `SerialApiLatency`、`JobSingle`、`JobBatch` |
| `Schedule`、`WorkerExecution`、`Complete` | `JobSingle`、`JobBatch` |
| `MeshApply`、`Commit` | `MainThreadCommit` |

`WholePipeline`だけはTarget別にModeを固定する。`SharedMeshCut`／`ConvexCut`／`TemporaryPhysicsProxy`は`SingleThreadKernel`／`JobSingle`／`JobBatch`、`UnityBakeMesh`は`SerialApiLatency`／`JobSingle`／`JobBatch`、3つのNative PhysX Targetは`SerialApiLatency`、`MeshPublish`／`ColliderCommit`は`MainThreadCommit`だけを許可する。さらにNative PhysX Targetの下位Stageはすべて`SerialApiLatency`、`MeshPublish`／`ColliderCommit`の下位Stageはすべて`MainThreadCommit`へ限定する。`JobBatch`は`BatchSize >= 2`、それ以外は`BatchSize == 1`を要求する。これにより`ColliderCommit + SingleThreadKernel`、`PlaneClassification + MainThreadCommit`等をCodecでRejectする。

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

### 7.6 片側空判定と共用Geometryの物理所属

切断操作の登録・対象世代更新・即時表示・Provisional生成より前に、受付Snapshotで現在の論理切断対象に対応する採用Physics Convex集合を7.1と同じ平面分類へ渡す。この一回の分類結果をNo-op判定と受付後のProvisional Shape配分で共有し、同じSnapshot・面・epsilonの用途別分類を作らない。全頂点が明確に片側ならその側だけへ、横断・epsilon内・分類不能なら両側へ配分候補を置き、操作全体で一方の候補が0件の場合だけ片側空No-opとする。これは共用Geometryとの非交差証明ではなく切断の受付規則であり、表示Geometryの張り出しを面が通過しても片側空ならGeometryを切らない。7.2の内接削減で当たり範囲が縮み、表示だけの部分が切れなくなることも正式に許容し、二次受付判定や当たり範囲の復元を行わない。面と交差するConvexがないことと片側空を混同せず、出力Convex、交点、Cap、接続成分を生成して判定しない。

No-opではLogicalCutOperation、論理子、CutBoundaryRecord、Pending Cutを作らず、ObjectGeneration、点Anchor、表示、Physics Proxy、運動、Constraint、既存所属を変更しない。即時clip／Stencil／仮Cap／Shadow近似、分離Offset／Impulse、Geometry／Convex切断、cook、Commitも起動しない。以前からある履歴・Pending仕事は維持し、省略した面を後から再生しない。命中観測と既存の非破壊通知は残せるが、No-op専用ID、履歴、Trace Eventは追加しない。

物理Convex実装前のPhase 1～2だけは、共通入力Gateに合格したFixtureの低頂点1 Hullを同じ入口のPlaceholderとして使用できる。Phase 4以降は現在採用中のConvex集合とlocal poseに一本化し、No-op専用Bounds、代替Hull、その選択Mode・生成・更新・包含検証を製品経路へ残さない。候補検索、描画、Cap Bounds、質量近似、予測Gate等の他用途Boundsは変更しない。Provisional中や単一Group採用中も、その時点で物理へ使用しているConvex集合を入力とし、未完成の最終Convexを同期生成したりcookを待ったりしない。

実命中では、先行成果物のReady状態にかかわらず同じ基底、採用面、現在入力Convex、local pose、epsilonと受付負荷からNo-op／受付を判断する。予測時の判定を再利用する実装はこれらの入力一致を確認し、不一致なら実命中時の現在入力で同じ分類を行う。受付後に有効な先行成果物と適用条件が揃えば直接確定でき、未完成なら同じ面の既存仮処理と後追い処理を使う。精密Convex処理で片側出力が空になっても入口のNo-opへ遡及せず、7.6の確定空規則で完了する。

通常切断では、通常の形状・質量・cook・公開条件が成立した正負Convex集合の空／非空だけで、物理出力と非空Geometryの所属を決める。

| 正側Convex集合 | 負側Convex集合 | 物理出力とGeometry所属 |
| --- | --- | --- |
| 非空 | 非空 | 正負各1所有者。各側Geometryは同じ側へ所属 |
| 非空 | 正常に空 | 正側1所有者。両側Geometryは正側へ所属 |
| 正常に空 | 非空 | 負側1所有者。両側Geometryは負側へ所属 |

空はその側のConvex集合全体の正常な計算結果を指す。一つの元Convexの片側が空でも同Sideに他Convexがあれば非空である。cook失敗、不正Convex、Weight不成立、未計算を空とみなさず、既存の物理採否・回収を使う。全出力の消去を新しい正常結果にしない。

反対側へ付属してもGeometryの面集合、winding、切断Side、局所配置を反転・消去せず、追従先だけを対応付ける。専用Convex、包含・接触・被覆、表示単位のConvex対応や所属候補探索を要求しない。同じ所有者へ付く正負のIndex範囲はそのまま使用し、実Capと履歴を維持する。独立したGeometry所属不能、専用Convex生成、極小Rigidbody、表示消去、恣意的な質量移送を設けない。

採用集合は既存の世代・参照・物理構成の成立条件を満たすことを前提とし、不正な物理・参照をGeometry所属で救済しない。既存物理を正式採用した場合もその採用済み構成への追従として完了し、新しい所属探索や来ないFinal成功待ちを作らない。表示・Stencil・再切断は同じ所属・世代・frameを使用し、物理適用は表示転送を待たない。Actor pose／速度を表示都合で巻き戻さず、所属切替時の表示の位置・姿勢差は許容する。

固定は7.1のCellが継承する点Anchorだけで決め、Geometryの反対側付属でAnchorを移送しない。質量・慣性は7.2の採用物理所有単位に適用し、独立物理が成立しなければ有効な既存構成を正式採用する。Collider、次回切断B-rep、Geometry追従先、資源所有権を一致させ、不要なProvisional参照は既存の安全な境界で退役する。物理終端は未完了のGeometry仕事の完了を意味しない。

受付後の通常計算で、一方の共用GeometryとPhysics Convexがともに確定空なら、その側を正常な空出力として扱う。確定空は実Capと面積0 Triangleを含む面集合が0件であることを通常成果物から判断し、面積・体積0、投影上のつぶれ、Stencilの相殺・非描画、未計算、失敗を空の証拠にしない。空側のRenderer、Collider、Rigidbody、所属Fallback、ダミーGeometry、架空Cap、cook、消滅VFXを作らず、不要な仮処理を退役する。非空側と実在境界を5.6に従って確定し、Fragment、CutBoundaryRecord、`LogicalCutOperation`を原子的に公開する。非空側の必要なGeometry／Physics Commitと仮処理の退役を終えた後に受付済み切断を正常完了し、未完了件数を一度だけ除く。子1件または境界0件を許容し、受付前No-opへ戻さず、世代、過去の有効履歴、採用先の物理状態を巻き戻さない。

共用Geometryが確定空でも有効なPhysics Convexが残る物理所有単位は、通常の物理条件、必要なcook、質量、支持を適用し、Rendererを持たない通常物体としてCommitする。空Renderer、ダミーMesh、専用物体種別、逆向きのGeometry―Convex対応、回収Metadataを作らない。Phase 5.5までは独立した遅延回収機構を実装せず、この物体は既存のObject／Level寿命まで物理作用と費用を持って存在できる。後続の任意の物理GCは7.9に従い、無効・延期・対象外なら同じ通常寿命まで残せる。GC予定を理由に本節の初回生成・必要なcookを省かない。

### 7.7 切断受付上限

受付済みでGeometry／Physics処理とCommitが未完了の切断に、既存容量設定の`MaxIncompleteCutOperationCount`を適用する。対象LogicalFragmentへの一回の受付済み切断を1件として全対象の合計を数え、実行待ち、実行中、依存待ち、Commit待ちを含める。子Geometry数、Convex数、Job数では多重計数せず、片側空No-op、受付見送り、Operation公開前の親／仮表示領域への後続要求、未命中の投機候補、任意のCollider Upgrade、7.9の任意分割・物理GCは数えない。

Main Thread上の共通受付処理はPending Cut登録、対象世代更新、即時表示、Provisional生成より前に現在件数を検査し、上限未満の場合だけ受付と件数加算を同じ境界で行う。上限以上なら対象ごとに切断を見送り、対象状態、世代、表示、支持、物理、既存仕事を変更せず、新しい論理子、境界、LogicalCutOperation、Pending Cut、切断／cook／Commit仕事を作らない。同じ斬撃波の一部対象だけが切れることを許容し、見送った要求を保存、再実行、一括予約しない。

受付済みの有効な切断はWorker時間、同時実行枠、cook枠、Queue満杯、締切超過だけでは取り消さず、既存Pending／DAGで実行可能になるまで保持する。表示CPU出力の完成だけでは件数を減らさず、現在の追従先・frame待ち、転送待ち、Geometry Commit待ちも既存Pending Cutと必要仕事を維持する。物理が先に完了すれば表示仕上げを待たず物理Commitし、表示の残工程を継続する。有効なCPU範囲の転送と現在の追従先・frameの公開条件が揃った場合は、4.5.6に従ってGeometryだけ先にCommitして対応Temporary描画を回収できるが、残る物理工程の受付情報と未完了件数を維持する。必要な表示・物理両工程の正常完了、または既存規則による終端と安全な回収後にPending Cutを退役させ、件数を一度だけ減らす。Operation公開前の終端失敗では4.2に従って当該Pending Cutだけを退役させ、実行中Jobと成果物を安全に回収した後に一度だけ減らす。二重計数・二重減算、専用Scheduler、overflow Queue、待機用IDを追加しない。実キュー満杯時だけ4.4の切断Coordinator例外を使え、形状不正、資源確保不能、物理Fault、世代失効は負荷待ちと区別して既存の安全規則で扱う。

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

本節の物体は、一つの物理運動・所有単位と、そのConvex、共用Geometry、関連するLogicalFragment集合を指す。初期処理単位は原則1 Actor相当とし、複数Actorを含み得るFragmentGroupや同じObjectIdの全Siblingを無条件に一括対象にしない。開始・適用時の両方で次を確認する。

- 表示／Stencil共用GeometryとPhysics Proxyが確定し、所属・frame・参照が有効である。LogicalCutOperation公開済み、またはGeometryだけCommittedでは足りない。
- 当該構成を変更する受付済み切断、未完了祖先制約、Anchor配分、Provisional引渡しがなく、通常の物理状態を採用している。Fault Frozenの復旧を本節へ委ねない。
- 計算用の不変な局所Geometryと現在の物理frameへの写像を保持でき、形状・scale・所属・支持の変化を既存の世代／前提照合で検出できる。共通の剛体並進・回転だけで静止を要求せず、適用は現在pose／速度へ接続する。

Collider Upgrade等と所有構成を同時に書き換える対象は初期は見送ってよい。読み取り専用の予測Jobだけを理由に永久に対象外にはせず、7.9.4の失効・資源寿命で扱う。条件確認不能なら元物体を残し、判定修復や専用待機状態を作らない。

準備中も元物体を正本として動かし、任意処理の完了を通常切断の受付・成立・公開の条件にしない。共有資源の競合による処理開始・完了の遅延は7.9.5の範囲で許容する。同一対象の任意変更は既存Main Threadの受付・Commit順で直列化し、公開直前に前提を再確認する。通常切断の受理、別更新または退役が先行した候補は公開せず、未Schedule仕事は既存取消、実行中仕事は完了後の不採用・回収へ送る。候補を最新対象へ推測適用しない。No-opや受付見送りで対象が不変なら、それだけで候補を失効させない。

#### 7.9.2 追加空間分割

成功は、一つの物理所有単位から、それぞれ非空の共用Geometry、有効な非空Convex集合、正規質量、既知の支持を持つ通常物体2個を得ることとする。Rendererだけの分割や、全Geometryを再び同じ所有者へ戻す結果は成功としない。2はこの追加分割一回の出力物理所有単位数であり、閉Component数や系譜・Scene全体のActor数ではない。各出力に複数閉Componentを含めてよい。表示なし第三物体や小さい側の生成省略は追加しない。

初期方式は固定された対象局所frameの一枚の平面で、現在の共用面集合を横切らず正負の非空集合へ配分する。大きなIndex範囲も内部をTriangle単位等で調べ、既存数値許容内で欠落・重複なく配分できることを確認する。既存Boundsが使える箇所は使うが、通常切断へ全島列挙を前倒ししない。探索は有限の試行・作業量で打ち切り、GJK、全組合せ、最良平面、完全分離を要求しない。候補なし・確認不能は7.9.5へ送る。

既存の表面・実Cap・面積0 Triangleを属性・winding・Topologyを保って配分し、新しい表示切断面やCapを生成しない。分割先別の新Index領域へ振り分けコピーし、必要範囲を転送する。比較ソートは必須でなく、Countと正負への書出しでよい。既存Vertexを参照し、全頂点複製、表示専用第二Index正本、全プールコンパクションは要求しない。新旧Indexの一時共存を許容し、旧範囲はJob・GPU・共有読者寿命後にFreeする。この処理を通常切断へ戻さない。

現在採用中のPhysics Convex B-repを同じ平面で分類し、非交差ConvexはB-rep・再利用可能なcook済み資源・Weightを継承する。交差Convexだけ7.2のclip、検証、質量計算、必要なcookを行い、共用Geometry全体の凸包再構築、Convex Union、未切断Shapeを両側へ複製するProvisionalを作らない。表示Geometryの対応有無にかかわらず全採用Convexを切断子または非交差継承として配分する。片側有効Convexなし、正質量不成立、実cook・検証不成立、資源・構成の成立不能では候補全体を不採用とし、元物体を残す。一時的な枠不足は7.9.5と区別する。

共用Geometryは今回配分した同じ側の物理所有者へ所属させる。反対側付属で二物体化を偽装しない。点Anchorは入力Cell集合から7.1の正負／OnPlane両側分類で継承し、その所有者が一つでもAnchorを持てば固定、なければ動的とする。Geometry付属をAnchor移送に流用しない。

Anchor配分と通常の物理条件を成立させて公開する。建物由来の分割には7.2.2を共用し、両子へIsBuildingDerivedを継承して公開時にDepthを親から1段進め、Anchorなしの動的子だけに新しいWorld D6を生成する。親D6は付け替えず、親Actorとともに退役する。必要な子D6の構築が不成立なら候補全体を不採用とし、親Actor・Depth・D6基準姿勢・Limitを維持する。単純な点Anchorの固定を保持できる対象を一律除外しない。

親Budgetは対象物理所有単位に確定した正規質量とWeight系譜だけを使い、共通祖先の全Sibling分を再配分しない。非交差Weight継承、交差Convexの子体積比、各出力のfiniteな正Weight和・正質量・慣性、親質量保存は7.2に従う。Geometryの個数・面積・寸法から質量を作らない。公開直前の現在pose／速度へ接続し、共用GeometryのWorld配置を通常の数値許容内で保つ。動的子には7.2の初回物理分裂の速度継承式、固定側には既存無運動規則を使い、攻撃・分離ImpulseとOffset、親poseの巻戻しを加えない。

両出力のGeometry参照、Convex／cook資源、質量・点Anchor、通常Actor・必要な建物D6と登録先を準備し、安全なMain Thread／物理Step境界で所有構成を一括切替する。描画は切替前か切替後の完全な構成だけを使い、片側先行公開、親の先行削除、表示／Stencilの別時点移管をしない。準備・公開前検証の不成立では未公開資源だけ回収する。

現在のLogicalFragment／Cell表現で、各Geometry範囲の再切断対象・frame・支持・物理所有者を一意にする。必要な通常IDと子配分情報は既存の発行・系譜規則で作り、過去IDの再利用、過去Operationの直接子・作成時世代の書換えはしない。既存の切断履歴・実在境界を維持し、現在世代への対応を原子的に更新する。新しいGameplay Cut Plane、CutBoundary、HitConfirmed、LogicalCutOperationは作らず、対応を安全に構築できない候補は見送る。

公開後は専用分割片ではなく通常の切断対象とし、現在の共用Geometry・Convex・Anchor集合から再切断する。旧Actor・そのシステム所有Constraintと不要Shape／Mesh参照は7.9.4に従って退役する。公開後の実Faultは既存の安全規則へ従い、任意候補専用rollbackを追加しない。

#### 7.9.3 表示なし物理物体の遅延回収

物理GCはManaged GCや全参照追跡型GCではなく、共用Geometryが確定空の生存物理所有単位を終了するゲーム上の寿命Policyである。7.9.1を満たし、所属する共用Geometry全体の面集合が0件で、有効な非空Physics Convexを持つ対象だけを候補とする。実Cap・面積0 Triangle・全Renderer／submesh相当範囲を含め、画面外、遮蔽、Renderer無効、Stencil相殺・非描画、体積0、未完成・失敗を空の証拠にしない。

一面でも共用Geometryが残る物体は回収せず、個別Convexを間引かない。Geometry／Convexとも確定空の通常出力と仮Actor・一時資源は従来の回収へ送る。対象自身の建物D6はActorと同じ寿命で退役する。Anchorなしだけを回収許可にしない。

通常の外界接触や移動だけでは回収を禁止しない。当たり判定消失による周辺物体の運動変化を許容し、接触中の完全無影響証明、Sleep必須、過去Impulseの取消を要求しない。開始・適用時の候補条件が一致する場合だけ、安全な物理境界で当該所有単位とそのLogicalFragmentの生存・受付、Physics Scene、所属Query／Occupancy等の登録を終了する。同じObjectId・祖先履歴を持つ無関係Siblingは削除しない。

生存中の通常物体を丸ごと終了する接続は今回の追加責務とし、汎用破壊システムが既存とは仮定しない。登録解除・Actor／Shape／システム所有Constraint退役・後着成果物不採用を既存担当へ結び、資源は7.9.4の寿命に従う。回収対象の質量は世界から除去し、Siblingへの移送・再正規化をしない。分割の親質量保存と寿命終了を区別する。

候補検出は既存物体一覧の有限件数巡回と確定Geometry件数等でよい。毎Frame全Mesh走査、逆被覆対応表、専用Asset前処理、VFXは追加しない。巡回順・頻度の高度な最適化や回収期限を初期要求にしない。

#### 7.9.4 非命中公開・退役と既存世代への接続

本節の所有構成変更・退役だけは、命中を伴わない公開を許可する。候補は既存Task／WorkTokenと成果物所有者、対象のObject・Geometry・Physics・Anchorの必要な世代・構成参照で識別する。準備・見送り・不採用だけではObjectGenerationを進めず、同じ基底の通常予測を失効させない。

追加分割の公開では対象の既存ObjectGenerationと実際に変更するPhysics等の既存世代を同じ境界で進める。GCは退役を既存の生存性／世代付き参照へ反映する。いずれも旧所有構成への予測・Upgrade・後着成果物を適用しない。履歴ID・作成時世代を書き換えず、世代wrap・ID再利用・別対象への古い適用を許さない。世代を共有する範囲の他の受付済み必須切断まで失効する場合は任意Commitを見送り、新しい世代軸や広域lockで解決しない。

通常切断のHitConfirmed・受付・採否条件は緩めず、本節の仕事へPending Cutを作らず、MaxIncompleteCutOperationCountにも数えない。通常切断が先に受理されたら任意成果物を不採用にし、任意変更が先に公開済みなら以後の切断は更新後の対象へ適用する。中間の曖昧な受付対象を公開しない。

登録終了と最終資源解放を分け、Schedule済み仕事の完了・回収責任と入力保持を維持する。移管した参照、他の生存対象・履歴が必要なMetadata、参照中の共用Geometry／Cooked Geometryを解放せず、旧Actor・Shape・システム所有Constraint・Meshは既存のStep・Job・GPU寿命に従って一度だけ退役する。全履歴圧縮、全メモリ即時解放、専用世代別GC、物理巻戻しを要求しない。

独立Maintenance世代、専用ID空間、Registry、永続形式、第二の状態機械は設けない。通常IDと生存Workの所有情報は既存規則を使う。観測は既存Task lifecycleと少数Counterで種別・成功／見送り・不採用・回収数を区別し、切断成功数や架空のCut Operation Trace束へ混ぜない。

#### 7.9.5 単一試行・再試行抑止と予算

無効な機能は候補走査も追加Jobも発行しない。候補計算、Convex処理、必要なcookは既存Job／CompletionとBackgroundMaintenanceを使い、Class 0～3のReady仕事がなくWorker／Bake枠・Tick予算に余裕がある4.4のアイドル条件でScheduleする。この条件はSchedule後の無干渉を保証せず、共有Queueの容量競合と、Schedule済みの非preemptive Job／cookによるWorker／Bake枠の一時占有・投入済み費用を許容する。そのため後から到来した通常切断の処理開始や完了が遅れてよい。通常切断の必須Class・予約枠、追加予約枠、preemption、別Scheduler、専用CPU使用率監視、overflow Queue、必達期限を使わない。

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

無効時の通常切断を維持し、有効時は分割成功後に通常再切断でき、GC後は退役対象が復活しないことを完了条件とする。追加機能固有の確認は次の少数合成Fixtureに限る。T-007／T-059／T-061／T-074／T-086／T-091は各担当の世代競合、引渡し、点Anchor、資源寿命の検証を再利用し、Provisional固有契約や全matrixを本節へ複製しない。

| 確認群 | 最低限の期待結果 |
| --- | --- |
| 独立した有効化 | 両方無効・分割のみ・GCのみで通常切断が成立し、無効機能の候補仕事なし |
| 分割成功 | 離れた二つの閉Geometryを一つのConvexがまたぐ例と非交差Compound例で、探索から必要cook・公開まで1件の試行で進み、通常所有者2個を生成。実Capを含む面集合・World配置・正規質量・再切断基底を維持。単一Index範囲の内部に両出力の面がある場合も新範囲へ配分・転送し、使用中の旧範囲を早期解放しない |
| 分割見送り | 単一平面で分離不能、面近傍、片側有効Convexなし、質量不成立、容量／cook・必要な建物D6構築不成立で元物体不変。部分公開・架空境界・Geometry消去なし |
| 支持・frame | 点Anchorを持つ島と持たない島が同じ所有者ではともに固定され、分割後はAnchorがない側だけ動的になる。OnPlane継承と旧共有資源からのAnchor非復活を確認する |
| GC選択 | 真の面集合0だけが候補。面積0 Triangleのみの非空、Renderer無効、画面外、未完了Geometry、可視Compound内の代表参照なしConvexは対象外 |
| 建物分割（Phase 5.6） | 成功時は7.2.2に従って建物由来の両子Depthを親から1段進め、Anchorなし動的子だけに新D6を生成して親D6を親Actorとともに退役する。不成立時は親Actor・Depth・D6基準姿勢・Limitが不変 |
| GC退役 | 接触中の表示なし物体を丸ごと回収し質量移送なし。対象Actorとその建物D6を既存寿命後に一度だけ退役する。共有資源・過去履歴・無関係Siblingを誤解放しない |
| 競合・寿命 | 通常切断・別更新後に古い成功公開と失敗抑止書込みを拒否。任意公開後の旧予測・Upgradeも拒否。二重公開・解放・切断件数変更なし |
| 再試行抑止 | 同一入力の不成立後は再探索せず、通常切断・GCは継続。関係入力変更で再対象化し、剛体移動だけでは再試行しない。未実行・Queue不足は不成立にしない |
| 低優先・容量 | cook・適用・不採用回収を遅らせても未回収試行は全体1件以内。完了回収は新規投入のアイドル待ちにせず、Compound cook大量投入なし。利用可能枠0・Queue満杯でも同期回復・昇格・無限再投入なし |

専用Test ID、旧Shared／Debris試験の復活、大規模Benchmark、P50／P95／P99製品保証を追加しない。探索方式、保存layout、巡回頻度、製品予算、効果の実測は実装・測定時に決め、未決を通常切断の不成立やPhase 6着手の障害にしない。

## 8. 世代管理と非同期制御

各SlashはGestureのLatch時に単調増加する`SlashGeneration`を持つ。各切断対象は確定状態を示す`ObjectGeneration`を持ち、通常切断では`SlashFront`のSweepによる実命中が確認され、Pending Cutを登録した時点でだけ更新する。空振り、候補列挙、投機ジョブ開始では対象世代を進めない。確定後の任意分割の公開・物理GC退役だけは7.9.4の非命中経路へ従い、準備・不採用では世代を進めず、通常切断の命中・Pending照合を緩めない。

投機ジョブは開始時の`BaseObjectGeneration`、`SlashId`、`SlashGeneration`、命中した`FrontEdgeId`、`SlashFrame`に加え、点Anchor配分を使用する場合は`AnchorGeneration`を保持する。点Anchor配分の結果を採用する際は、既存入力Snapshotと世代契約で同じ基底、採用面、入力Cell、frame、継承済みAnchor集合および`anchorEpsilon`との一致を確認する。所有者単位の固定／動的は配分された集合の有無から導出し、専用の成果物型や状態を要求しない。ジョブを強制キャンセルするのではなく、完成時およびCommit時に、実命中と各識別子・世代・前提条件を検証する。一致しない成果物はコミットせず破棄し、安全に再利用できる中間資産だけを回収する。これらの照合に新しい世代軸を追加しない。

状態は物理分裂、Geometry完成度、非同期Work Result採否を分ける。固定／動的は採用物理所有者のAnchor集合から決め、未完了計算は既存Work依存で表す。

| Object／FragmentGroupの物理状態 | 意味 | 許可される処理 |
| --- | --- | --- |
| Stable Unsplit | 今回の物理分裂を行わず、切断前の単一FragmentGroup、または既に原子的公開済みで利用可能なActor／Collider構成を正式採用した | 新規切断の物理基底に使用し、同じ未解決問題を時間だけで再試行しない。未完了のGeometry処理や非同期仕事は別に完了させる |
| Pending Physics Split | Provisional構築不能時の保守Fallback。FragmentGroupの1 Rigidbody／旧Colliderを共有し、表示と論理破片だけが分離済み | Convex生成とBakeを待ちながら、後続切断と外力をGroup全体で受理 |
| Pending Anchored Split | Provisional構築不能時の保守Fallback。固定側分類済みだがCollider未分裂で、旧Colliderを固定したまま自由側だけを衝突なしで仮表示 | Anchor配分結果を維持し、完全Convex切断とBakeを待つ。共有物理へ切断Impulseを与えない |
| Provisional Physics Split | 全子Detachedと確定後、子ごとのRigidbodyが旧cook済みConvexを再利用して外界Collisionと分離運動を先行するが、Final Collider／質量特性は未完成 | Sibling Collisionを無効化し、Provisional Constraintで再侵入を抑えながら後続切断、外力、非同期cookを受理 |
| Provisional Anchored Split | 点Anchor配分から固定／動的が定まった各物理所有者がProvisional Actorを持ち、Anchored子は固定、Detached子だけがDynamic。Final Collider／質量特性は未完成 | 固定側のOffset／Impulseを0に保ち、自由側の外界Collisionと分離運動、後続切断、非同期cookを受理 |
| Provisional Fault Frozen | 公開後の非finite、速度上限超過、Constraint破綻をGroup単位で封じ込めた不可逆な物理安全状態 | 全Actorを直前のfinite姿勢でKinematic化するかGroup全体をPhysics Sceneから除外し、Constraint、外力、新規物理分裂、Final handoff、自動復帰を禁止する。表示Geometryの背景処理と最終破棄だけを許可 |
| Stable Fast Cook | Fast Cook Colliderで物理分裂済み | 通常物理を継続し、必要なら低優先度Upgradeを予約 |
| Physics Upgrade Pending | 別MeshをFast Simulationで再Bake中 | 現Colliderを維持し、世代変更時はUpgradeを破棄 |
| Stable Fast Simulation | Fast Simulation Colliderへ安全に差し替え済み | 長寿命・高接触破片として通常物理を継続 |

各物理所有者は継承AnchorがあればAnchored、なければDetachedとして扱う。固定側のOffset／Impulseは0とし、点配分が未完了なら物理適用をWork依存で待つ。表示の可否はこの区別に依存しない。

| `CutBoundaryRecord.GeometryState` | 意味 | 許可される処理 |
| --- | --- | --- |
| Pending | 有効なCPU Geometry出力が未完成 | 5章の未Commit面・Sideによる仮表示を継続 |
| Ready | 有効なCPU Geometry出力が完成 | 4.5.6に従い必要転送を行い、現在有効な追従先・frameと公開条件が揃えばCommit。ReadyだけではTemporary描画を外さない |
| Committed | 共用VP Geometry参照の表示公開に成功済み | 対応Temporary描画だけを同時に回収。物理工程の完了は含意しない |

WorkResultState.Readyは個々のWork成果物の完成であり、集合としての表示採用可能性ではない。AllocatorのPublishedはCPU読取り公開であり、GPU転送やGeometry Committedではない。

| 非同期`WorkResultState` | 意味 | 許可される処理 |
| --- | --- | --- |
| Scheduled | Work Itemが予約済み | Job開始またはSchedule前取消を待つ |
| Running | Jobが実行中 | 完了結果を生成し、直接Unity Objectを変更しない |
| Ready | 成果物が完成し、Commit検証待ち | 最新の前提・各Generationと照合 |
| Stale | 完成時点で前提または世代が古い | Commitせず、必要なら再利用可能な中間資産だけ回収 |
| Committed | 有効な成果物を境界タイミングで適用済み | 二重Commitを禁止し、解放処理へ進む |
| Disposed | 成果物と一時領域を解放済み | 以後の適用・参照を禁止 |

CutBoundaryRecordは少なくともCutBoundaryId、CutPlaneId、必要な子Geometry・Side・frame参照、GeometryStateと作成時ObjectGenerationを持つ。CutPlaneIdは採用面／local frame identityへ結合し、19.5.1のSourceSlashPlaneとSelectedObjectLocalCutPlaneを混同しない。Geometry Commit後も再切断と表示に必要な面・写像・履歴を保持する。境界を支持Edgeや必ず異なる二所有者の対として管理しない。

Kerfは0、固定側のOffset／Impulseは0とする。固定を理由に仮描画を省略せず、同一位置の正負Capを通常Colorで常時両面描画しない。

## 9. リグ付き人形の切断

関節をフリーズできるため、切断時点でアニメーション世界から静的破壊世界へ移送する。実際の現在姿勢とボーン行列をスナップショットし、4.5.2の同期経路または有効な先行準備から共通VP入力へ合流して、同じ採用Poseの即時clipと一般プロップと共通の切断処理を使う。Phase 5は先行準備なしの同期経路で基本切断を完了し、Jobベイクの導入採用時だけPhase 5.1で先行成果物の再利用・未完成／成果物不採用時の通常経路を統合する。表示開始時に残る準備費用と表示開始フレームは4.5.2に従う。

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

表示の実行時表現は4.5に従い、読み込み時Unity Meshから切断時VPへ移行する。Component単位のGeometry参照を保持し、複数参照による表示構成と論理・物理所属を分離する。Component識別用参照と描画用Descriptorを区別し、実切断結果は4.5.6の正負の共通Index配置へ仕上げる。ZCG v1はFixture形式のままとし、Runtime AoS形式への改訂や既存Fixtureの一括再生成を要求しない。

7.9の後続任意処理は本章の既存Geometry／Cell／Anchor入力を再利用し、専用Asset分類、Shard、逆被覆表、全Asset再生成、Schema改訂を要求しない。

| 層 | 用途 | 品質契約 |
| --- | --- | --- |
| 共用Cut Geometry | 通常表示、最終破片、即時Stencil Volume | 6章の閉鎖・edge／vertex manifold・局所winding整合済みTopology。複数submesh、Disconnectedな閉Component、Component間のIntersection／Overlapを許容 |
| 幾何Topology Metadata | 共用Geometryの切断・Cap生成 | 6章の局所隣接とContourを維持し、物体化のための全島列挙を要求しない |
| Physics Proxy | 接触とConvex切断 | 少数の低頂点Convex／Compound。各Convexは有効な閉凸形状だが、Compound内の相互Overlapを許容 |
| 固定Anchor入力 | 所有者全体の固定 | 固定を設定する入力だけが点Anchorの初期Logical Convex CellとfiniteなFrame内local位置を明示する |

Blender側ではTransform適用、原点・単位統一、共通Material、三角形化、共用Geometryの閉鎖・manifold・winding検証、Compound Physics Proxy生成、Unity書き出しをプリセット化する。幾何切断・Asset処理に必要なComponent情報だけを保持し、部品間の接着情報は生成・保存・読込み・検証しない。入力の食い込みへBoolean Unionや全体inside-outside検証を要求しない。固定を設定する場合は点Anchorの初期Cellとfiniteなlocal位置を出力し、未指定の固定や対応を推測しない。

共用Cut Geometryは表示／Stencil／Closed Componentの唯一のGeometry正本であり、役割別に並行更新・切断するUnity Mesh、基底、派生物、適否、Cacheを作らない。4.5.1のGPUコピー・作業構造・旧世代・投機Snapshot・未採用VPは許容する。製品Asset Schema、Preprocess Cache、Build、Runtime FallbackはStrict Solid用の参照や生成物も持たない。

#### 10.2.1 建物用Structural Slab候補

Poly Pro Universe実AssetのBlender人力調査から、典型的な建物を装飾付きの厚い壁板4枚以上で外周構成する近似は、Boolean Unionや建築構造解析を要求せず自動化しやすい候補とする。各`StructuralSlabComponent`は独立して閉鎖・切断・CapできるRender／Stencil Componentと、原則1個の直方体Physics Convexを持ち、建物1周分を同じ固定FragmentGroup／Compound Rigidbodyへまとめる。入口や通行可能な開口を1箱で塞ぐ場合だけ、左右と上部等の少数箱へ分割する。窓枠、柱、モールド、看板等は通常の共用Geometry Componentとして同じ所有者へ所属させ、装飾Geometryを構造Convexへ忠実に反映しない。

建物Recipeは7.2.2のIsBuildingDerived=trueと初期BuildingSplitDepth=0をPhase 5.5で出力する。Phase 0.2のFixture／Schemaへ前倒しせず、製品切断対象は同節の一般外部Jointなしの登録契約に従う。

各Slabの下端両側を点FixedSupportAnchor候補とし、製品Recipeでは初期Slab Convex Cellとfiniteなlocal位置を明示する。切断後は所有者単位のAnchor有無で固定する。途中の離れた部品も同じ側にAnchorがあれば固定されたままでよく、1回または任意の2平面で必ず独立・落下する保証を置かない。

Phase 0.2では個別建物の最終Recipeを作らず、Poly Pro Universeの`Building`カテゴリから10.2.2の`phase02-building-v2`による元Sourceの自動screening合格だけを処理対象へ選ぶ。人間による個別membership分類は行わず、矩形footprint候補としての偽陽性・偽陰性を許容する。窓枠、柱、モールド、看板、入口の張り出し、複数Object／Componentを人間判断だけで除外・救済せず、固定adapterと測定規則を適用する。対象内のGeneric Geometryから大きな平面Component、保守的OBB、building-local Transform、canonical外周順を抽出し、10.2.2の薄い`StructuralSlabFixture` sidecarとして後段の壁板Geometry・Convex確認へ渡す。Selectedには4 Slab以上を要求する。製品用の点Anchorと入口保持はPhase 5.5のAsset Recipeで確定し、早期成功を全Buildingまたは全Assetへ一般化しない。

#### 10.2.2 Phase 0.2のFixture生成・選抜

Phase 0.2はpilotで得た知見から、後続Phaseが再探索せず使用できる少数の入力Geometry、用途別Binding、選抜根拠を確定する工程である。製品Preprocessorを前倒しせず、Licensed TriangleMesh／単一Hull Convex／薄い構造sidecarを非公開Datasetへ、既知正解のSynthetic Fixtureを公開Datasetへ分離する。Licensed Strict Solidの生成・検証・成功率は要求しない。一般的なConvex decomposition（VHACD等）、複数Hull Compound、実際の切断／Cook、Collider差し替え、質量・慣性、Ground Anchor生成は対象外とする。

本隊とは本repositoryとそのPhase 0.2先行branchを含む。正式成果物は、採用した最小処理を本隊のversion付きScript／Preset Bundleへ移植し、固定Blender 4.5.12 Windows x64から生成する。merge後に同じ成果物を再生成する義務はない。使い捨てpilot repository／commit／extensionは要件抽出と移植判断の参考に限り、設計正本・再生成依存・正式provenanceにしない。pilot生成Geometryを直接正式Datasetへ採用する例外は設けない。移植するものは最小algorithm、Preset、少数の回帰入力と期待される意味的結果、既知制限／timeout／失敗分類だけとする。

Phase 0.2の設計正本は本DESIGNだけとし、別文書の承認済み部分や先行branchの実施計画が本DESIGNを上書きする例外を設けない。実装前に先行branchのDESIGNを本隊の承認済みPhase 0.2契約および関連する下流参照へ同期する。実施計画は操作手順、task分割、実験・運用記録に限って補助資料として残せるが、必須成果物、schema、Gate、完了条件を追加・変更する場合は先にDESIGNへ反映して承認する。先行branchの`PHASE_0_2_ARCHITECTURE_CANDIDATE.md`はPhase 0.2完了時に削除する一時的な実施計画であり、本隊に存在しなくても本DESIGNだけで要件を判断できることを要求する。完了時はScript／Preset／Schema／Goldenと正式provenanceだけで下流読込・再生成・再開が成立することを確認して当該実施計画を削除する。そのpath／content hashを正式Bundle、Dataset、Receiptまたは後続Phaseの実行依存にしない。

##### カテゴリ別入力・Recipe

`EarlyFixtureSourceCatalog`はVariant生成前に母集合、Source hash、Asset root／Object階層、Category、stratum、`Phase02Eligibility`、version付き`EligibilityRuleId`と`ScopeReason`を固定する。Categoryは`Character／Building／Vehicle／Environment／Props`の完全列挙とする。Syntyはvendor FBX、Poly Pro Universeはvendor `.blend`を入力とし、pilot加工済みfileをSourceに置き換えない。vendor Collectionとroot＋descendantによるAsset境界を固定adapterで抽出し、名前の曖昧なprefix一致だけで別Assetを結合しない。

| Category | Phase 0.2入力Scope | 許可する初期処理 |
| --- | --- | --- |
| Character | PPUだけ。Synty Characterは事前Scope外 | static bind-poseのOriginal／cap候補だけ。Direct Decimate／Voxelを生成せず、skin weight／bone pose／AnimationをZCGへ含めない |
| Building | PPUの自動screening Eligibleだけ（10.2.1／10.2.2） | DepthWallを基底にSurfaceFill OFF／ONを独立した形状生成軸として保持し、各出力からN-triを作る。低ポリ化optionと混同しない |
| Vehicle | Bodyと車輪等の既存Exception Object階層を保持 | `VoxelSolidify1cm`。meter基準0.01 m、Separate per Source Object。基底を保持し、Presetで最大Variant数を固定した限定Post-Decimateだけを追加 |
| Environment | PPU Tree／Rockだけ | Original／capと元より少ないTriangleへのReduction。カテゴリ固有処理の小Pilot成功を拡大前Gateにする |
| Props | Synty `SM_Prop_*`とPPU Props | Original／cap／Reduction。床貼付の非破壊板はversion付きfamily＋preflight形状規則で生成前にScope外。板／細長／直方体／その他のstratumは偏り防止用であり製品分類ではない |

Excluded SourceはCatalog、理由別集計およびEligibilityに必要なRule固有preflight判定記録へ残し、生成Recipeの予定Variant／Attempt／Geometry Rejectを生成しない。preflight測定は生成Attemptとは区別する。最終ReportはReceiptの完了frontier内のBatch Manifestにある全Source／全予定Variantだけを過不足なく被覆する。未割当・frontier外Sourceは最終Report Entryを持たず、割当／実行状態はManifestと再開用記録から識別する。全CatalogのGeometry生成や全Asset互換を完了条件にせず、成功結果やReviewを見てEligibilityを変更しない。Asset別の手修正・局所最適化・成功見込み順への並べ替えを行わない。

Originalを上書きせず、Import、Transform／単位Bake、三角形化、重複・退化・孤立要素の最低限cleanup、面向き再計算は由来を保持した派生処理とする。Geometry identityはZCG bytesとし、`.blend`保存bytesをAlias／Geometry Cache keyへ使わない。

`BoundaryLoopFill`と`BlindNonManifoldFill`はOriginalから独立生成する。前者はObject／Topology Componentごとに面隣接1のBoundary Edgeを抽出し、全頂点次数2の閉Loopを最小Topology ID順に処理する。Open Chain、分岐、共有頂点、穴径／平面誤差／個数超過を推測修復しない。後者は固定BlenderのNon-Manifold Select、F相当、三角形化の全Operator引数をPresetへ固定した探索経路とする。別Object／ComponentのUnionや頂点結合を行わず、一方の失敗を独立Variantへ伝播させない。Blind成功およびその全派生物は`BenchmarkOnly`を上限とする。閉Solid／自己交差なし／体積一致の証明はLicensedに要求しない。

Reductionの`Tri100／Tri500／Tri1000／Tri2000／Tri5000／Tri10000`は要求Preset名であり、実規模はZCG後の`ActualOutputTriangleCount`とする。許可されたカテゴリ／親Recipeについて`RequestedDecimateRatio = TargetTriangleCount / ActualInputTriangleCount`をbinary64で1回だけ計算し、1回適用後に再三角形化する。Target合わせの反復、頂点追加、局所手修正は禁止する。入力がTarget以下なら直接入力Artifact参照のNoOp、同一出力hashならAliasとし、上回れば1 Triangle差でも生成を試みる。Environment／Propsでは元より細かくするVariantを追加しない。Propsの初期Reductionは0.75／0.5を優先し、0.25以下はPresetで固定した補助形状Gate合格時だけ選抜する。具体的なTarget／Ratioの許可行列と上限はversion付きPresetで処理前に固定し、実行結果を見て増やさない。

Vehicleは相対Bounds解像度のVoxel64／128／256を廃止し、1 cm専用Recipe／Report fieldを使用する。Voxel基底はTriangle数が減る／同じ／増えるだけでは省略しない。BuildingのDepthWall／SurfaceFill／Reduction出力はGeneric TriangleMeshであり、名称がCollisionでも自動的にPhysics入力にしない。Originalに対するBounds・中心移動・表面偏差と用途Gateで表示適性を評価し、意図的な凹部消失・開口封鎖も無条件に代表Renderへ採用しない。

##### Artifactと用途の分離

生成過程、形状、用途、選抜を独立に表す。旧`Tier`、`GeometryProcessMode`、`QualityClass`による連動分類を正式選抜契約から除く。

| 軸 | 値／責務 |
| --- | --- |
| `ProvenanceClass` | `Synthetic／LicensedThirdParty`。配布・由来の区分 |
| `GeometryKind` | ZCGの`TriangleMesh／ConvexSet` |
| `RecipeKind` | `Original／BoundaryLoopFill／BlindNonManifoldFill／DirectDecimate／DepthWall／SurfaceFill／VoxelSolidify1cm／SlabBox／ComponentConvexHull／ProceduralTriangleMesh／ProceduralConvex／StructuralSlabExtract`の完全列挙 |
| `RecipeVersion`・親参照 | 処理version、正確な親Artifact ID／content hash。未知Recipeと欠落・別Source・循環参照を拒否 |
| `ProcessStatus` | 予定処理の結果。`PlannedVariantEntry`だけが保持 |
| `SelectionClass` | 成功Artifact／sidecarに対する`Unselected／Selected／BenchmarkOnly` |
| `FixtureBinding` | 正確なArtifactまたはsidecarと1個の`FixtureRole`を結ぶcanonical record。同じGeometryを複数用途で使う場合もbytesは複製しない |

`EarlyFixtureSelectionReport.PlannedVariantEntry`はSource、Batch ordinal、Variant順、Recipe、Attempts、`ProcessStatus`を持つ。Statusは`Succeeded／Rejected／ProfileUnsupported／ResourceDeferred／ToolFailed`の終端5値に固定する。`Succeeded`は`OutputArtifactKind`、`OutputArtifactId`、`PlannedVariantResultKind`を必須とする。出力tagは`Geometry／StructuralSlabFixture`、GeometryのResult Kindは`Generated／NoOp／Alias`、sidecarは`Generated`だけを許可する。`StructuralSlabExtract`だけがsidecarを出力し、それ以外はGeometryを出力する。非Succeededでは出力3 fieldを持たず失敗Reasonを必須とし、Fixture Binding／Dataset採用を禁止する。成功Geometry／sidecar自体にはProcessStatusを重複保持させない。

NoOpは直接入力Artifact、Aliasは同じSource／GeometryKind／ZCG hashのcanonical Variant順で最初の先行Artifactを出力参照とする。自己参照・前方参照・循環・hash不一致を拒否し、別のAliasTarget fieldは設けない。Result Kindだけを根拠に成功Artifactを消したり、NoOp／Aliasに新しいGeometry fileを作らない。Recipe経路の`BenchmarkOnly`制限はAlias／NoOpを経由しても解除されず、同一bytesへの別BindingによってBlind由来を昇格させない。

| Geometry／sidecar | Recipe | 許可FixtureRole | 許可SelectionClass |
| --- | --- | --- | --- |
| Licensed TriangleMesh | Original、BoundaryLoopFill、DirectDecimate、DepthWall、SurfaceFill、VoxelSolidify1cm | `RenderCutInput` | Selected／BenchmarkOnly |
| Licensed TriangleMesh | BlindNonManifoldFillおよびその派生経路 | `RenderCutInput` | BenchmarkOnlyだけ |
| Synthetic TriangleMesh | ProceduralTriangleMesh | `RenderCutInput／PhysicsCorrectnessInput` | Selected |
| Licensed ConvexSet（単一Hull） | SlabBox、ComponentConvexHull | `PhysicsCookInput` | Selected／BenchmarkOnly |
| Synthetic ConvexSet（単一Hull） | ProceduralConvex | `PhysicsCookInput／PhysicsCorrectnessInput` | Selected |
| StructuralSlabFixture | StructuralSlabExtract | `StructuralSlabInput` | Selected／BenchmarkOnly |

表にない組合せを拒否し、BindingなしのUnselected中間Artifactは許可する。共通Geometry Gate合格だけではRoleを付けず、各Role Gateを再検証する。TriangleMeshへPhysicsCookInputを付けない。SyntheticとLicensedは同じ形状Codecを使えてもReport／Dataset／保存rootを混在させない。

Phase 0.2のLicensed `RenderCutInput`は探索・Benchmark用の用途Bindingであり、Dataset採用だけでは6.2の共通入力Gate合格または製品Runtimeへの登録を意味しない。Phase 1～3で共用切断へ渡す少数入力は描画・計測のホットパス外で6.2を確認し、合格入力だけを通常切断へ渡す。Phase 0.2のRecipe行列、選抜quota、ZCG v1、既存Datasetをこのために再設計しない。

##### StructuralSlabFixtureと単純Convex

Buildingの成功Generic Geometryに薄い`StructuralSlabFixture` sidecarを付ける。必須情報は`StructuralSlabFixtureId／BuildingSourceFixtureId／GeometryArtifactId／SlabCount`、および外周順のSlab列である。各Slabは一意の`SlabLocalId`、元Geometry／Component ID、building-local Transform、平面と外向きnormal、保守的OBBのcenter／axes／extents、連続した`PerimeterOrdinal=0..SlabCount-1`、抽出Recipe／品質計測を持つ。任意のPlacementClassは外周順序の代替にしない。全Slabが同じSourceとGeometryに属することを照合し、Selected StructuralSlabInputには`SlabCount >= 4`を必須とする。製品用の点Anchorと意味分類は持たせない。

BuildingはsidecarのSlabごとに独立Boxを1個作り、SlabBox Artifactから正確な`StructuralSlabFixtureId + SlabLocalId`を参照する。Vehicle／Propsは既存Object／Componentごとに単純Convex Hullを1個作る。Phase 0.2のConvexSetは`HullCount == 1`だけを受理し、自動分割・複数Hull化・Compound組立てで不合格を救済しない。

Phase 0.2用ZCG Loader／VerifierのCodec制約は`MaxDecodedVertexCountPerHull=255`、`4 <= V <= 255`とする。閉凸多面体について`FaceCount <= 2V - 4`、全Faceのindex数合計`FaceVertexIndexCount <= 6V - 12`を頂点数から導出する割当安全上限とし、独立したFace数Profile parameterを設けない。これに加え既存の閉鎖Topology、凸性、向き、正のfinite体積を検証する。`PhysicsCookInput`は別のRole Gateとして`MaxCookInputVertexCountPerHull=128`を要求し、`ConvexPolygonCount <= 252`を導く。129..255頂点の有効ZCGは保持できるがPhysicsCookInput Bindingを付けない。この128は早期Cook入力のRole Gateであり、Runtime登録・切断出力の上限は7.2で独立して定める。本節のCodec・Role Gateと既存Fixtureは変更しない。Cook成功自体はPhase 0.25で測定する。

##### Gate・Attempt・Profile

Geometry／選抜の数値と固定順位は`EarlyFixtureSelectionProfile`とversion付きPresetに記録する。初期共通値はabsolute／relative epsilon各`1e-6`、Bounds diagonal `0.001..1000 m`、非zero軸2以上、Source／保存基底Triangle上限は後述D-155の`2000000`、通常Variant／RenderCutInput Triangle上限は`200000`、最大Component `256`とする。Boundary Loopは穴径`min(0.02 * diagonal, 0.05 m)`、平面誤差`max(0.001 * loopDiameter, 0.00001 m)`、最大16 Loopを初期値とする。代表Renderの各軸extent偏差5%、Hard Bounds偏差25%、中心移動5% diagonal、双方向固定4096 sampleの表面距離P95 2% diagonalを初期Gateとし、version付き実装・seed・tie-break・比較方向・退化Boundsの扱いを正式生成前にGoldenで固定する。代表Gate外でもHard Gate内はBenchmarkOnly、Hard Gate外はRejectedとする。Licensed Renderへwatertight、全Mesh自己交差、Solid体積誤差Gateを追加しない。Topology診断と補助volume診断は合否の必要条件と混同しない。

**Source／未削減基底の保存と用途上限（D-155）。** Phase 0.2のLicensed Sourceと未削減保存基底は三角形化・既存cleanup後のTriangle数で最大2,000,000とし、polygon／quadのface数を上限単位にしない。保存基底として許可するRecipeはOriginal（全カテゴリ）、BuildingのDepthWallとSurfaceFill OFF／ON、VehicleのVoxelSolidify1cmだけに限定する。全Object合計へ適用し、Objectごとに上限を別枠化しない。EarlyFixtureSelectionProfileの上限fieldはMaxSourceTriangleCount=2000000、MaxRetainedBaseTriangleCount=2000000、MaxVariantTriangleCount=200000、MaxRenderCutInputTriangleCount=200000とし、固定Recipe許可表とともに新しいversion付きProfile／hashへ記録する。通常のFill／Reduction等のVariantは200000を維持し、Recipe名や保存先だけで基底例外を取得できない。Source計数は通常Import後に行い、Catalog Freezeへ全Source計数を戻さない。

**Selection Profile v2の完全root定義。** 上限専用Profileは作らずEarlyFixtureSelectionProfileへ統合する。canonical rootは次表の全11 propertyだけを記載順に持ち、全て必須・非nullとする。任意fieldや未列挙の「既存field」はない。旧ProfileのID／hashを再利用しない。

| 順 | Property | 型・許可値 |
| --- | --- | --- |
| 1 | SchemaVersion | JSON integer、2のみ |
| 2 | ProfileId | string、phase02-selection-retained-base-v2のみ |
| 3 | MaxSourceTriangleCount | JSON integer、2000000のみ |
| 4 | MaxRetainedBaseTriangleCount | JSON integer、2000000のみ |
| 5 | MaxVariantTriangleCount | JSON integer、200000のみ |
| 6 | MaxRenderCutInputTriangleCount | JSON integer、200000のみ |
| 7 | RetainedBaseRecipeRules | 下記の厳密5 object配列 |
| 8 | GenerationPresetReferences | 下記Preset参照の非空配列。使用するRecipe Presetへの直接参照 |
| 9 | GeometryRoleGatePresetReferences | 同参照の非空配列。使用する共通Geometry／用途Gate Presetへの直接参照 |
| 10 | SelectionPresetReferences | 同参照の非空配列。固定順位、Triangle帯、quota、tie-breakを網羅 |
| 11 | ReproducibilityAuditPresetReferences | 同参照の非空配列。既存監査sample選定・tolerance・計測設定を網羅 |

各Preset参照objectはRelativePath／ContentSha256の順で厳密2 field、両方必須・非nullとする。RelativePathはcanonical相対file path（最大1024 UTF-8 byte）、ContentSha256は当該canonical文書全体の小文字64桁SHA-256。Profileの構文検証だけを正式実行許可とせず、参照検証へexact Preset Bundle root、CanonicalBundleIndex v1（BundleKind=Preset）、期待PresetBundleContentSha256および同じ実行のScript Bundle identityを必須入力として渡す。期待hashは実行前に固定したEntry.Provenance.PresetBundleContentSha256（Entry生成前は同じ予定実行contextの値）とする。Indexのcanonical bytesのhash一致、Indexとrootの完全file集合・長さ・hash一致を既存Bundle Verifierで確認してから、参照pathをそのrootだけで解決する。別Bundle探索、path検索、schema推測を禁止する。Profile自身も同Index中のexact path／hashへ照合するが、Profile本文にBundle Index hashを逆参照させない。

4配列は設定文書への直接参照だけとし、共通Rules wrapper、RuleKey、Ruleごとの実装path／hash／Configuration参照を設けない。実装identityは既存Entry.Provenance.ScriptBundleContentSha256で固定する。各Presetは対応する既存または小さな専用Loaderで検証し、Profile側でvalidatorを登録・結合する仕組みを作らない。Schemaの選択は以下の固定ProfileId／SchemaVersionだけに従い、未知ID／versionを推測して受理しない。全配列はRelativePath ordinal昇順、同配列内pathとProfileIdは一意、各参照objectは既述のRelativePath／ContentSha256だけを持つ。

| 配列 | 許可ProfileId／SchemaVersion | 件数・使用条件 |
| --- | --- | --- |
| GenerationPresetReferences | Original: phase02-original-generation-v1／1、BoundaryLoopFill: phase02-boundary-loop-fill-v1／1、DirectDecimate: phase02-direct-decimate-v1／1。その他はBlindNonManifoldFill: phase02-blind-nonmanifold-fill-v1／1、DepthWall: phase02-depth-wall-v1／1、SurfaceFill: phase02-surface-fill-v1／1、VoxelSolidify1cm: phase02-voxel-solidify-1cm-v1／1、SlabBox: phase02-slab-box-v1／1、ComponentConvexHull: phase02-component-convex-hull-v1／1、StructuralSlabExtract: phase02-structural-slab-extract-v1／1 | 1..10件。実行前の予定Variantで使うRecipeのPresetを各1件直接参照する。OFF／ONや複数Targetは対応Preset内の既存設定を使い、別wrapperを作らない。Recipe固有の既存実行前チェックで必要Presetを確認する。未実装RecipeのLoaderをこの変更のためだけに先行実装せず、当該Recipe実行前に固定する |
| GeometryRoleGatePresetReferences | phase02-common-geometry-gate-v2／2、phase02-render-cut-input-gate-v1／1、phase02-physics-cook-input-gate-v1／1、phase02-structural-slab-input-gate-v1／1 | 1..4件。共通Gateは厳密1件、残りは予定用途で使用するものを各1件直接参照する。各Gate呼出し時に既存の専用Loaderで確認する。共通Gate旧v1の200k制限を2M入力へ暗黙流用しない。上限値はSelection rootと一致させる |
| SelectionPresetReferences | phase02-selection-preset-v1／1 | 厳密1件。カテゴリ別固定順位、Triangle帯、quota、tie-breakを保持する小さな設定文書。Rule単位に分割しない |
| ReproducibilityAuditPresetReferences | phase02-reproducibility-audit-preset-v1／1 | 厳密1件。既定sample選定・完全一致項目・数値tolerance・除外項目を保持する小さな設定文書。Rule単位に分割しない |

新規追加するSelection／Audit Presetは各1文書だけとする。Selection Presetのroot順はSchemaVersion／ProfileId／CategoryVariantOrder／TriangleBandUpperBounds／MinimumCategorySourceCounts／MinimumCoveredTriangleBandCount／MinimumPhysicsUniqueSourceCount／TieBreak。SchemaVersion=1、ProfileIdは上表の厳密値。CategoryVariantOrderはCategory／VariantIds順のobjectを既定5カテゴリ順に持つ厳密5行配列で、VariantIdsは当該カテゴリの既存予定Variant IDを重複なく既定の選抜順位に並べる（最大256件／行、IDは既存規則）。TriangleBandUpperBoundsは[224,707,1414,3162,7071,200000]、MinimumCategorySourceCountsはCategory順Character／Building／Vehicle／Environment／Propsのobjectで値2／2／2／2／4、MinimumCoveredTriangleBandCount=5、MinimumPhysicsUniqueSourceCount=6、TieBreakは[SourceFixtureId,VariantOrdinal,ArtifactId]。既存の多様性条件・Binding制限・NoOp／Alias非加算は既存選抜処理に残し、新たなRule文書で重複表現しない。順位は結果を見る前に固定し、選抜結果に合わせて再記入しない。

Audit Presetのroot順はSchemaVersion／ProfileId／MinimumSourcesPerCategory／RequireEnvironmentTreeAndRock／ExactMatchFields／TransformAbsoluteTolerance／TransformRelativeTolerance／BoundsDiagonalToleranceRatio／TriangleAbsoluteTolerance／TriangleRelativeTolerance／SurfaceSampleCountPerDirection／SurfacePercentile／SurfaceDistanceDiagonalToleranceRatio／ExcludedObservationFields／RegenerateReviewPresentation。SchemaVersion=1、ProfileIdは上表の厳密値、順に最低1、true、[ProcessStatus,LogicalVariantGraph,Recipe,LogicalParentReferences,SelectionClass,ComponentCount]、0.000001、0.000001、0.01、16、0.02、4096、95、0.02、[WallClockMilliseconds,PeakWorkingSetBytes,PeakWorkingSetObservationStatus,RunId,AttemptCount]、falseとする。sample選定・seed、論理参照比較、finite確認、方向別P95、正規化尺度は既存の監査処理と固定Script Bundleを正本とし、別のRule／実装hash参照層を追加しない。

両新規Presetは本節のcanonical JSON共通規則に従う。上記root順を完全順序とし全field必須・非null、数値は記載の整数／finite number、フラグはboolean、列挙配列は記載の厳密string列。未知／欠落／重複field・順序違反・固定値不一致を専用Loaderで拒否する。各文書は64 KiB以下で、配列確保前に上限を検査する。新しい汎用「完全被覆」機構を作らず、既存Recipe／Gate／選抜／監査の各入口で必要な設定と既存予定集合の一致を検査する。Resource／Allocation／Reviewの独立契約は維持する。


上限4値は順にJSON integer `2000000／2000000／200000／200000`だけを許可する。RetainedBaseRecipeRulesは次の厳密5行の配列とし、各行のproperty順はCategory／RecipeKinds、値と配列順は`[{"Category":"Character","RecipeKinds":["Original"]},{"Category":"Building","RecipeKinds":["Original","DepthWall","SurfaceFill"]},{"Category":"Vehicle","RecipeKinds":["Original","VoxelSolidify1cm"]},{"Category":"Environment","RecipeKinds":["Original"]},{"Category":"Props","RecipeKinds":["Original"]}]`へ固定する。SurfaceFillのOFF／ONは既存Presetの両設定を含み、新しいRecipeKindを追加しない。既存Recipe／Category／親参照検証とこの許可表の両方を満たす場合だけ基底上限を使用する。未知・欠落・重複・順序違反・固定値不一致を既存canonical Codecで拒否する。Geometry Artifact／Report／Datasetのschema、Catalog／Allocation Profileは変更せず、参照するSelection Profile hashとprocessing cohortだけを更新する。

root・参照objectとも未知／欠落／重複property、null、順序違反を拒否する。Profile全体は既存64 KiB上限、配列は確保前に上限を検査し、既存canonical UTF-8 JSON規則と再serialize一致を適用する。root自身のcontent hash fieldは設けない。この完全列挙は既存Presetの数値やalgorithm変更を要求せず、少数Codec／Goldenで確定する。

上限内で既存Geometry／Hard Gateを満たす基底は、正式ZCG、Geometry Artifact、PlannedVariantEntry、正確な親ID／hashとprovenanceを保持する。VehicleのVoxel基底を内部中間物だけにする案と複合Recipeへの置換は採用しない。Post-Decimateは保存済み基底を直接親とし、既存の限定Preset・1回Ratio・NoOp／Alias規則を維持する。基底保存成功後の子失敗で基底を削除せず、再処理で検証済み基底を再利用できる。200000超の保存基底はSucceeded／Unselected、用途Bindingなしとし、BenchmarkOnlyへ落とすことでRenderCutInput上限を回避しない。200000超の保存基底は、選抜された子Artifactに必要な場合だけ既存の正確なBinding親closureとしてDatasetへ収録する。Bindingなし基底を任意にDatasetへ追加する規則は設けず、Dataset caseやquotaとして数えない。親closureに不要な基底も正式ZCG／Geometry Artifact／Reportとして保持して再処理へ利用できるが、Datasetだけの移送対象には含めない。200000以下の基底は既存Role Gateによる採用を妨げない。PhysicsCookInputは別に生成したConvexSetへ既存128頂点Gateを適用し、TriangleMeshの保存上限からPhysics品質を推論しない。用途Bindingの200000超過はBinding拒否であり、有効な保存基底をRejectedへ変更しない。

Source／基底が2000000、通常Variantが200000を超えた場合は既存の決定論的Geometry上限違反としてRejectedとし、反復Decimate、Target変更、resource retryで救済しない。32 GiBの単一Blender Working Set監視、初回120秒・timeoutまたは観測済み32 GiB Working Set超過だけ最大1回300秒retry（D-154）、非並列、Voxel予算、Component上限256、有限値・Topology・Bounds等の既存Gateを維持する。保存上限以下での生成成功は保証しない。Allocation Profile／Catalog／Batch割当は変更せず、処理Profile改訂は新cohortとして既存記録を上書きしない。少数再現性監査から基底Geometryを除外する変更ではない。

未削減保存基底のReview appearanceはTriangle数によらずCategory／Recipeで固定する。Character／Environment／PropsのOriginalはImportされた既存Material／Texture／UVを変更せず使用するExistingAppearanceとする。Material未割当slotだけ固定Neutral材質を使い、描画対象に割当Materialが全くない場合だけ全体をNeutralGeometryとする。未割当slotが混在してもMixedAppearanceへ分類しない。BuildingのOriginal／DepthWall／SurfaceFill OFF／ON、VehicleのOriginal／VoxelSolidify1cmは既存Textureの有無にかかわらずNeutralGeometryに固定する。それ以外の通常の削減後等のGeometryは既存のReview専用UV／2048 atlas／BakeによるBakedTextureとする。既存のCategory×Recipe許可検証に不合格の組合せをこの分岐で救済しない。

ExistingAppearance経路はImport結果をそのまま描画し、Material slotごとのrenderer対応可否・UV利用可否を分類する新しい機構を持たない。正式参照されたTextureの欠落、hash不一致、読み込み失敗をNeutralで隠さずReview Presentation失敗として記録する。UV修復、Texture探索、Material変換、Texture転送、再Bakeによる救済は行わず、Human Reviewや実行結果による個別経路切替を禁止する。参照資源検証そのものを省略する意味ではない。ExistingAppearance／NeutralGeometryでは新規UV展開、atlas packing、Bakeを実行しない。既存Materialが表す葉の透過等もこの経路のために不透明へ上書きしない。表示不能や画像からの形状判定不能は既存Presentation失敗とし、Geometry Gate／ProcessStatus／SelectionClass／Dataset membershipへ伝播させない。

同じappearanceで全11方向の通常画像とmesh overlay版の対を生成し、Textureあり／Neutralの二重Suiteは作らない。neutral材質は既存ReviewPresentationProfileの固定Preset内に非金属・不透明・Base Color RGBA=(0.5,0.5,0.5,1)、roughness=1として定め、照明・camera・overlayは既存設定を使う。下記ReviewPresentationRecord v1の末尾の必須ReviewAppearanceModeはBakedTexture／ExistingAppearance／NeutralGeometryの3値だけとし、上記分岐から機械的に記録する。MixedAppearanceは受理せず、slot単位のappearance分類結果は記録しない。変更したPresentation Profile／script hashで旧Review cacheを区別する。Neutral画像は元Texture再現ではなく形状監査用と明記し、silhouette・欠損・封鎖・surface・mesh乱れのHuman Review、Geometry hashとの相関、画像欠落・判定不能の既存Presentation失敗契約を維持する。Review fieldはGeometry Artifact／Report／Datasetへ追加せず、再現性監査でReview生成を行わない境界も維持する。

**Review Presentationの最小canonical境界。** 画像生成設定をReviewPresentationProfile v1、完成画像の対応記録をReviewPresentationRecord v1として明示する。Human Reviewの自由記述／rubric回答を新しいReportへ移行する要求ではない。両文書とも本節のcanonical UTF-8 JSON規則、全property必須、未知／欠落／重複／順序違反拒否、canonical再serialize一致を使用する。

ReviewPresentationProfile v1の全root順はSchemaVersion／ProfileId／ViewCount／ImagePairKinds／BakeTextureSize／NeutralBaseColor／NeutralMetallic／NeutralRoughness／PresentationScriptRelativePath／PresentationScriptContentSha256。値は順にinteger 1、string phase02-review-presentation-v1、integer 11、厳密string配列[Appearance,MeshOverlay]、integer 2048、厳密number配列[0.5,0.5,0.5,1]、number 0、number 1、Script Bundle内canonical相対path、同fileの小文字64桁SHA-256とする。nullはない。相対pathは最大1024 UTF-8 byte、文書上限4 KiB。11方向の順序・camera・照明・overlay・appearance固定分岐は当該hash固定scriptの定数を正本とし、別の可変設定を探索しない。既存画像生成実装を固定scriptとして用い、今回のためにcamera設定schemaを新設しない。

ReviewPresentationRecord v1の全root順はSchemaVersion／GeometryArtifactId／GeometryContentSha256／SourceAppearanceContentSha256／PresentationProfileRelativePath／PresentationProfileContentSha256／PresetBundleContentSha256／ScriptBundleContentSha256／PresentationScriptContentSha256／Images／ReviewAppearanceMode。SchemaVersionはinteger 1、GeometryArtifactIdは既存Artifact ID規則、各Sha256は必須小文字64桁、Profile pathはexact Preset Bundle内canonical相対pathとする。SourceAppearanceContentSha256は新しいappearance探索Indexを作らず、対応Sourceのexact Source Bundle Index全体のhashを使う（不要なSource変更でcache missすることを許容）。Imagesは順序固定の厳密22 object配列。各objectの順はViewOrdinal／ImageKind／RelativePath／ByteLength／ContentSha256、ViewOrdinalはinteger 0..10、ImageKindはAppearance／MeshOverlay、各view内でこの順の2件とする。pathは当該Review root内canonical相対path、ByteLengthは正integer、hashは画像file全体の小文字64桁SHA-256。ReviewAppearanceModeは末尾の必須stringでBakedTexture／ExistingAppearance／NeutralGeometryのみ。nullなし、path最大1024 UTF-8 byte、record上限64 KiB、Images確保前に件数とbyte上限を検査する。

recordは成功した完成22画像だけを表す。画像生成失敗は既存Presentation失敗診断に残し、成功recordを捏造しない。再利用時は呼出側のexact Geometry、Source／Preset／Script Bundle identity、Profileとscriptのpath／hash、固定分岐から得たmode、22画像のpath／長さ／hashを全て照合する。旧version、field欠落、MixedAppearance、hash不一致、画像欠落はcache hitにしない。旧cacheを暗黙移行せずReview資料だけを再生成し、Geometry再生成・選抜変更は要求しない。Profile／recordと画像は非公開Review rootで管理し、Geometry／Dataset schemaや内容hashへ追加しない。

T-078～T-081の既存少数Fixture／fakeへ、Source／許可基底の200000超保持、2000000境界、通常Variant／RenderCutInputの200000境界、許可外Recipeの例外拒否、選抜された子に必要な基底だけの既存親closure収録・case／quota非加算、親closure再読込と再利用を追加する。byte／PositionCount上限は宣言headerによる割当前拒否を含めて検査し、2M実Assetの反復生成や新しい性能SLAを要求しない。既存Vehicle pilot sample 1件で生成・保存・再読込みを確認し、全Asset再生成や追加の比較matrixを要求しない。未削減基底のBake呼出し0でも全11方向の画像対とReview記録が存在することをfakeで確認する。 少数fakeでCategory／Recipe固定分岐、200k以下／超でも同じ基底経路、OriginalのMaterialあり／なし／未割当slot混在、Building／VehicleのTexture有無によらないNeutral、通常Bake、参照Texture欠落／hash不一致／読込み失敗を確認する。MixedAppearance拒否、ExistingAppearance／NeutralGeometryでUV／atlas／Bake呼出し0、全11方向画像対、ReviewAppearanceMode一致と選抜非影響を検査する。Selection Profileの全11 root propertyと参照objectの順序・型・必須・null／未知／重複拒否、上限4値・厳密5行許可表・参照hash・配列上限を少数Golden／negativeで確認する。Rules wrapper／RuleKey／実装file hash結合のための試験は追加しない。Selection／Audit各1文書の専用Loaderと少数Goldenだけを追加し、Recipe／Gateは既存試験を再利用する。 同じ少数fakeでexact Preset Bundle不一致、4配列の許可ID／version・各件数条件・必要Preset欠落、Review Profile／Recordの完全property順・旧cache拒否・22画像相関を検査する。128 MiB適用はDecoderだけでなくEncoder／保存側にも同じ許可基底条件が渡ることを確認し、最大規模fileの反復生成は要求しない。

実装前にカテゴリRecipeの全許可行列、Gate、計測algorithm、固定選抜順位、Source Triangle帯／stratum、監査toleranceをProfile／Presetのcanonical fieldとして固定する。選抜は同じcohort内の数値Gateと固定順位だけで行い、同値の最終tie-breakはSourceFixtureId、Variant ordinal、Artifact IDのordinal順とする。別cohortの数値を無条件に順位比較しない。

各AssetのGeometry生成処理は隔離Blender processで、後述のEarlyFixtureResourceProfile v1に従い初回120秒、timeoutまたは観測済み32 GiB Working Set超過だけ最大1回300秒retry、Blender非並列とする。Pilot／Expansionとも単一Blender processのWorking Set定期監視による停止閾値を32 GiB（34,359,738,368 bytes）へ固定し、Peak Working Setは診断用に観測する。旧4 GiB制限、Pilotの上限なし、Expansionの上限探索・確定待ちは廃止する。Blind capも同じ共通上限に従う。決定論的な入力・形状・Profile上限をresource retryで救済しない。予定VariantごとにLaunch、Bootstrap、Import、Geometry処理、ZCG／Role Gateの到達StageとReasonを残す。形状違反はRejected、未対応のカテゴリRecipe／固定Profile能力外はProfileUnsupportedとする。最終AttemptがTimedOutまたは観測済みWorking Set閾値超過のMemoryLimitExceededの場合だけResourceDeferredとする。timeoutも閾値超過も確認されていないOS資源不足、起動・script例外・原因不明crash・不正出力はToolFailedとし、自動retryしない。失敗親に依存する予定子も原因参照と終端Statusを残し、独立Recipeは続行する。非終端の予定処理を成功とみなさない。

##### 固定batch・frontier・Human Review

事前Eligibilityを通過したEligible Sourceだけから、下記canonical契約のカテゴリ／stratum別7 queueを作る。Catalog Freeze時に構成可能な全均衡batchのEarlyFixtureBatchManifestを一括確定する。Excludedは投入件数を消費しない。Catalog内の固定順は全EntryのCatalogEntryOrdinal、割当はManifestのBatchOrdinal／BatchSourceOrdinalとし、未割当Sourceには後者2値を持たせない。割当済み・未実行Sourceの割当は保持し、完了frontierへは全予定処理が終端した連続batchだけを含める。件数は成功数ではなく投入数であり、Geometry実行量の一括確定を意味しない。

| Batch | Character | Building | Vehicle | Environment | Props | 計 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Pilot | 2 | 2 | 2 | 2 | 4 | 12 |
| Expansion | 1 | 1 | 1 | 1 | 2 | 6 |

これらを独立したEarlyFixtureBatchAllocationProfile v1の`PilotBatchCategorySourceCounts／ExpansionBatchCategorySourceCounts`へ固定する。PilotはEnvironment Tree1／Rock1、Props Synty2／PPU2。ExpansionはEnvironmentをBatchOrdinal奇数でTree、偶数でRock、PropsをSynty1／PPU1とする。必要stratumのいずれかが不足したら、そのbatchを作らず全queueを未消費のまま均衡拡大を止める。同一Sourceの全Variantと比較する兄弟を同じbatchへ入れ、全PlannedVariantEntryの終端後だけbatchを閉じる。割当規則の正確な順序と再開検証は下記EarlyFixtureBatchManifest契約に従う。

最低quota達成後も本隊がPhase 0.2へ到達するまで、一括確定済みManifestの未実行batchを順次実行できる。人間が最後の連続完了batchを明示Freezeし、`LastIncludedBatchOrdinal`、Catalog hash、対象prefixのBatch Manifest参照列をSelection Receiptへ固定する。Freeze後の追加実行は新Selection Run／Dataset revisionとし、既存Receiptへ追記しない。ResourceDeferredはbatchの終端結果であり、後のrevisionで再試行できる。最終Reportは対象prefix内の全Source／全予定Variantだけを完全被覆し、Catalog全体のEligible被覆を要求しない。

Human Reviewは事前version固定のrubricによる全方向のsilhouette、欠損、意図しない封鎖、surface、mesh乱れの監査であり、個別ArtifactのStatus／SelectionClass／membershipを書き換えない。問題は再現可能な数値Gate／Profile改訂へ変換する。既存Report／ZCG／画像で再評価できる場合は再生成せず、追加監査と再生成は影響Category／family／Recipeだけへ限定する。重大問題の範囲を絞れない場合だけ全再生成の要否を人間へ戻す。未解決の重大監査Issue中はFreezeしない。Review記録は非公開canonical監査資料として残せるが、自由記述をmembership入力にしない。

新Preset／Recipeは新しいBundle identityを持つ。処理済みArtifactの生成時hashを書き換えず、新旧versionは別cohortとして同じDatasetへ収録できる。既存GeometryへのGate再評価は生成provenanceと評価Profile identityを分離して記録する。新revisionは論理的な選抜確定であり、全batch再生成ではない。中断再開では完了Artifactのhash・親参照を検証して再利用し、未完了処理だけを再開する。

##### Source Catalog v2／Batch Manifestのcanonical契約

本項をSource Catalogとbatch割当の正本とする。JSONはBOMなしUTF-8、固定property順、未知property／enum拒否、余分な空白・末尾改行なしとする。hashは小文字64桁SHA-256、integerは非負のJSON整数とし、countは実配列長と一致させる。省略可能fieldは設けず、nullを許すのは明記したfieldだけとする。

**Profile責務の分離（D-151）。** Catalog／Batch Manifestの`BatchAllocationProfileContentSha256`は独立した`EarlyFixtureBatchAllocationProfile v1`のcanonical bytesだけをhashする。EarlyFixtureSelectionProfile全体やPreset Bundle全体のhashを代用しない。Allocation ProfileからSelection／Resource Profile、Recipe、Geometry Gate、選抜順位、監査tolerance、処理Script／Preset Bundleを直接・間接参照しない。Catalogが参照するEligibility Rule Set／preflight依存は固定したまま、処理側は別のimmutable Profile／Preset versionを使用できる。処理／評価／資源条件だけの改訂ではCatalogと全Batch Manifestを再Freezeせず、既存割当を保持してEntry単位のprocessing／evaluation cohortへ記録する。Source母集合・Eligibilityまたはその正本依存・割当規則を変える場合は新しいCatalog／必要なAllocation Profile identityとManifest集合にする。同一Sourceの全Variantとは「そのSourceから生成する全Variantが当該Sourceのbatchへ属する」という規則であり、Allocation ProfileへVariant一覧を埋め込まない。予定Variantは各処理cohortの実行前に確定し、旧Report／Receiptの予定集合を遡及変更しない。

`EarlyFixtureBatchAllocationProfile v1`は本項のcanonical JSON共通規則を使う独立文書とし、property順を`SchemaVersion／ProfileId／PilotBatchCategorySourceCounts／ExpansionBatchCategorySourceCounts／QueueOrder／PilotQueueCounts／ExpansionOddQueueCounts／ExpansionEvenQueueCounts／SourceOrder／InsufficientQueuePolicy／SourceVariantPolicy`とする。SchemaVersion=1、ProfileId=`phase02-batch-allocation-v1`。カテゴリ件数objectはCharacter／Building／Vehicle／Environment／Props順で、Pilot=`2／2／2／2／4`、Expansion=`1／1／1／1／2`。QueueOrderは`Character-General／Building-General／Vehicle-General／Environment-Tree／Environment-Rock／Props-Synty／Props-PPU`の厳密7文字列配列で、各queueの所属は既存CatalogのCategory／BatchStratumに従う。対応件数配列はPilot=`[2,2,2,1,1,2,2]`、ExpansionOdd=`[1,1,1,1,0,1,1]`、ExpansionEven=`[1,1,1,0,1,1,1]`の厳密7整数とする。奇偶はBatchOrdinalによる。SourceOrder=`SourceFixtureIdOrdinalAscending`、InsufficientQueuePolicy=`StopWithoutConsumption`、SourceVariantPolicy=`AllVariantsInSourceBatch`とする。v1はこれらの値だけを許し、カテゴリ合計とqueue配分の不一致を拒否する。上限4 KiB、未知／欠落／重複property・enum・順序不一致を拒否し、Loaderは上限以下の呼出側byte上限、配列確保前の固定長検査、canonical再serialize一致を要求する。自身のcontent hashは埋め込まず外部で計算し、Catalog／全Manifestと照合する。33 Manifestは今回の入力による実行計画でありschema固定件数ではない。

**Pilot／ExpansionのResource Profile。** `EarlyFixtureResourceProfile v1`も同じcanonical JSON共通規則を使い、property順は`SchemaVersion／ProfileId／BatchKind／InitialTimeoutSeconds／RetryTimeoutSeconds／MaximumResourceRetryCount／MaximumConcurrentBlenderProcesses／WorkingSetLimitBytes／WorkingSetPollIntervalMilliseconds`とする。SchemaVersion=1、ProfileIdは`[A-Za-z0-9._-]{1,128}`、BatchKindはPilot／Expansion、初回120秒、retry300秒、resource retry最大1回、同時Blender process最大1を固定する。timeoutは各Attemptの既存計測境界に適用する。WorkingSetLimitBytesはPilot／ExpansionともJSON integer `34359738368`（32 GiB）だけを許可し、nullおよび他の値を拒否する。WorkingSetPollIntervalMillisecondsはJSON integer `1000`（通常の監視周期1秒）に固定し、実時間の厳密な検出期限とはしない。初期ProfileIdはPilot=`phase02-pilot-resource-ws-poll-v1`、Expansion=`phase02-expansion-resource-ws-poll-v1`とし、BatchKindとの組合せを照合する。上限なし案の`phase02-pilot-resource-v1`および監視方式未確定の旧`phase02-pilot-resource-32gib-v1／phase02-expansion-resource-32gib-v1`を流用しない。将来閾値・監視周期を改訂する場合は明示的な仕様変更と新しいversion付きProfileId／hashを使用する。Pilot結果から上限を探索・確定する工程を置かず、既定Profileが有効ならExpansionを開始できる。文書上限4 KiB、未知／欠落／重複field、null条件違反、未定義値を拒否し、呼出側byte上限とcanonical再serialize一致を検査する。全Attemptは実際に使ったResource Profile hashを保持し、生成Entry provenanceはそのAttemptと生成Profile／Presetへ結合する。既存のRecipe／Geometry側のTriangle・Component・Voxel等の決定論的上限は撤廃せず、resource retryで救済しない。

32 GiBは起動した単一Blender processのWorking Setを定期監視する停止閾値であり、OSが強制する瞬間的なhard limit、process tree全体のcommit上限、ホスト全体の安定動作や全Asset完了の保証ではない。監視対象は当該Attemptが起動した正確なBlender processに固定し、子processや他アプリのメモリを合算しない。通常の監視周期はWorkingSetPollIntervalMillisecondsに従い、実測Working Setが34359738368 bytesを超えた場合だけmemory閾値超過として既存の停止経路を起動する。一致値は超過としない。監視間の一時的超過、検出から停止までの超過、監視遅延、子process等の対象外メモリを許容し、厳密なprocess treeメモリ制御・子process集計をこの要件のために追加しない。既存のtimeout時の終了・回収契約を弱める意味ではない。実機にはUnity／他アプリの使用分も含む余裕があることを前提とする。Peak観測は診断用であり、上限探索や性能試験を完了条件にしない。将来の監視設定変更もSourceを再割当せず別processing cohortへ記録する。Building Eligibility preflightの単一読み取り専用process・判定不能時Freeze停止は別契約であり、この生成用Resource Profileで上書きしない。自動resource retry対象は、timeoutを確認したTimedOut、または当該Working Set閾値超過を観測したMemoryLimitExceededの2種類だけとする。初回120秒の該当失敗に限り、同一Resource Profileで最大1回300秒retryする。最終AttemptがTimedOut／MemoryLimitExceededならResourceDeferred、ToolFailedならToolFailed、成功なら通常の成功として扱い、初回のresource失敗だけを理由に最終ToolFailedをResourceDeferredへ変換しない。その他の結果は既存のAttemptResult対応に従う。timeoutも閾値超過も確認されていないOS資源不足は、一時的または診断上明白でもToolFailedとして終端し、自動retryしない。原因不明crash・計測不能だけをメモリ超過と推測せず、起動・script例外・不正出力もToolFailedへ送る。Reason／logは診断用に残すが、新しいResourceExhausted enumやOS固有の資源不足判定機構は追加しない。後の明示的な再実行は許可し、既存Entry／Attemptを上書きせず新しい実行／cohortのprovenanceとして記録する。終了証拠を取得できないホスト停止では架空の終端結果を作らず既存の中断再開契約に従う。

Attemptのメモリ観測は`PeakWorkingSetBytes／PeakWorkingSetObservationStatus`をこの順の対へ一本化し、`PeakProcessTreeCommitBytes`をPhase 0.2のcanonical Attempt propertyから削除する。旧fieldを受理して無視したり、0／nullで残したりせず、Codec／Goldenも新しいproperty列へ同期する。Attemptの既存`MemoryLimitBytes`はResource ProfileのWorkingSetLimitBytesと一致する停止閾値の写しであり、process tree commit limitではない。Peakの値は取得時に0..9007199254740991の整数byte、未取得はnull、Statusは`Complete／Partial／Unavailable`だけとする。サンプリングしたWorking Setの最大だけを保存する場合は、全期間に定期監視を実行していても実際の瞬間Peakを証明しないためPartial／非nullとする。対象processの生存期間全体のPeakを別途確実に回収できた場合だけComplete／非nullを許し、その追加回収実装は必須にしない。部分期間の値だけなら同じくPartial、未取得はUnavailable／nullとし、未取得を0や完全Peakへ置き換えない。Partial／Unavailableは観測の完全性であり、それだけでGeometry成功を失敗へ変換しない。wall-clock、Peakとstatus、Run ID、Attempt数は再現性監査の一致条件から除き、観測値をDataset content hashへ加えない。使用Profile／Preset identityは別にprovenanceとして保持する。

**Attempt／EntryのResource Profile参照。** field名は両方とも`ResourceProfileContentSha256`へ統一する。Phase 0.2 ReportのAttempt property順を`AttemptOrdinal／TimeoutSeconds／MemoryLimitBytes／ResourceProfileContentSha256／ReachedStage／AttemptResult／Reason／ExitCode／WallClockMilliseconds／PeakWorkingSetBytes／PeakWorkingSetObservationStatus`とする。PlannedVariantEntry.Provenanceのproperty順は`SourceFileSha256／SourceGeometryContentSha256／BlenderBaselineId／BlenderExecutableSha256／ScriptBundleContentSha256／PresetBundleContentSha256／GenerationProfileContentSha256／ResourceProfileContentSha256／EvaluationProfileContentSha256／CohortId`とする。その他のfieldの型・null条件は既存契約を維持し、ResourceProfileContentSha256だけは両配置とも必須・非nullの小文字64桁SHA-256とする。未知／欠落／重複propertyと順序違反を拒否し、旧PeakProcessTreeCommitBytesを残したAttemptは受理しない。

ProvenanceのResourceProfileContentSha256は当該Entryの実行前に固定したResource Profileを指し、全Attemptは実際に使用した同じProfile hashを持つ。Verifierは各AttemptのhashとEntry.Provenanceのhashの完全一致、参照するPreset Bundle内の正確なResource Profile文書のcanonical bytes全体のSHA-256との一致を要求する。参照文書の欠落・改変、別BatchKind／ProfileId、AttemptのMemoryLimitBytesとWorkingSetLimitBytesの不一致を拒否する。AttemptOrdinal=0のTimeoutSecondsはInitialTimeoutSeconds、許可されたresource retryのAttemptOrdinal=1はRetryTimeoutSecondsへ一致させる。初回120秒とretry300秒は同一Profile内の設定なのでhashを変えず、同じEntryのAttempt列へ別Profileを混在させない。Profile自体を変更した再処理は新processing cohort／Entryの記録として分離し、既存Attemptのhashを書き換えない。

AttemptCount=0の未実行Entry（親失敗により実行しない場合等）も、Provenanceへ予定したResourceProfileContentSha256を保持し、参照Profileの存在・hash・対象Batchとの一致を検証する。予定hashの存在だけを実行成功の証拠にせず、実行の有無はAttempt列と既存ProcessStatusから判断する。架空のAttemptを追加して一致条件を満たさない。

**監査再生成の対象範囲。** Licensed reproducibility auditのclean regeneration対象は、少数固定監査Sourceの正式Geometry、sidecar、および対応するReport Entry・選抜結果だけとする。Review Presentation専用UV生成、atlas、2048 texture bake、全方向画像は再生成・等価性比較の対象外とし、監査実行経路から呼び出さない。Report全体のbyte一致や、非監査Source・Review専用結果の再生成も要求しない。正式Geometry生成・ZCG出力に必要なUV等の処理まで省略する意味ではない。元成果物に対する通常Human Reviewと初回Review Presentation生成は維持し、Bake省略をGeometry失敗・SelectionClass変更・監査証拠欠落として扱わない。Review資料は元の正確なGeometry hashに結合したものとして参照できるが、hashが異なる再生成GeometryのPresentationとして付け替えない。

**Licensed再現性監査の初期tolerance。** EarlyFixtureSelectionProfileまたはその参照するversion付き監査Presetへ、次の値と比較規則を監査開始前に固定する。基準は元の正式成果物、比較対象は同一Source・生成設定・Blender／Scriptと元のResource Profileによる上記対象範囲のclean再生成とする。Resource Profileを変更した実行は別cohortであり、同一条件再現の成功証拠へ混ぜない。最終EntryのProcessStatus、論理Variant graph、Recipe／生成設定、論理親参照、SelectionClass、Component数は完全一致する。論理親参照はSourceFixtureId・論理Variant識別子・Recipeと親子edgeの一致であり、再生成Geometryの親content hashや実行固有Artifact IDの一致ではない。元と再生成の各参照closureは、それぞれの実file hashに対して独立に検証する。

同じcanonical座標系・同じ要素順のlocal-to-source 4×4 Transform行列を要素ごとに`abs(a-b) <= max(1e-6, 1e-6 * max(abs(a),abs(b)))`で比較する。Boundsの中心・各軸extentは同じsource空間の対応成分それぞれの差が`0.01 * D`以下、Triangle数は`abs(Nregen-Nreference) <= max(16, 0.02 * Nreference)`とする。Dは派生Recipe前の共通元Sourceから固定測定したfiniteかつ正のBounds diagonal（meter）であり、再生成Boundsから取り直さない。Source identity／座標系・Dの測定規則を監査provenanceへ残し、Dが不明・非finite・0以下なら監査合格にしない。比較演算はbinary64、各入力／中間値のfinite確認を必須とし、表示丸めや位置合わせで差を消さない。元成果物と再生成Geometryの双方向surface distanceは既存の固定4096 sample方式を各方向へ適用し、両方向それぞれのP95が`0.02 * D`以下であることを要求する。sampling実装／seed／対応空間／tie-breakとP95のnearest-rank `ceil(0.95 * 4096)-1`を固定し、両方向を合算して片側の誤差を隠さない。距離計算の異常・対象Geometry不在を0距離で合格させない。この監査は元Source対派生Geometryの品質Gateとは別であり、既存の用途別Gateを緩和しない。

監査は既存の各Category最低1 Source、Environment Tree／Rock両方の少数固定sampleだけに適用し、全Licensed再生成やbytes完全一致を要求しない。tolerance内の形状差とP95で未検出の局所差を許容する一方、既存用途別Gate・Human Reviewを維持する。超過時はCategory／family／Recipe単位で影響範囲を調べ、直ちに全再生成へ送らない。tolerance変更は新しい評価Profile／cohortとし、監査結果を見て旧Profileを上書きして合格にしない。

T-078／T-081の少数Golden／fakeで、独立Allocation Profileの固定値・hash照合、Geometry／Resource／監査Presetだけの改訂でCatalogと全Manifest bytesが不変であること、Eligibility変更時は新Catalogになることを確認する。両BatchKindの32 GiB固定値・ProfileId照合、null／旧4 GiB／他値の拒否、1秒監視設定、fakeの閾値一致時非停止／超過観測時停止、TimedOut／MemoryLimitExceededだけの単一retryと最終同種失敗のResourceDeferred終端、未観測OS資源不足のToolFailed・retryなし、retry後ToolFailedをResourceDeferredへ変換しないこと、Peakの3取得status・sampling最大値のPartial扱い・旧PeakProcessTreeCommitBytes拒否、監査の完全一致項目・各数値閾値境界・論理親一致とGeometry hash差の許容・方向別P95を検査する。監査経路でReview専用UV／atlas／2048 bake／画像生成の呼出数が0であり、Geometry／sidecarと対応Entryの監査は行われることを小さいfakeで確認する。実OSメモリ枯渇の注入、全Asset再計測、新しい性能SLAや大規模Report体系は追加しない。 Attempt／Provenanceの完全property順、ResourceProfileContentSha256の欠落・null・大文字・長さ不正・値不一致・参照文書改変、同じhashの120秒／300秒retry、別Profile混在拒否、AttemptCount=0の予定Profile参照を少数Golden／fakeで検査する。

**Source Catalog。** `EarlyFixtureSourceCatalog` v2のroot property順は`SchemaVersion／CatalogId／SourceBundleContentSha256／EligibilityRuleSetId／EligibilityRuleSetContentSha256／BatchAllocationProfileContentSha256／EntryCount／Entries`とする。SchemaVersionは`2`、CatalogIdは`[A-Za-z0-9._-]{1,128}`、EntryCountは`1..100000`。EntriesはSourceFixtureIdのordinal昇順で一意とする。`SourceBundleContentSha256`はBundleKind=`Source`の正確なCanonicalBundleIndex v1のcanonical bytes全体のSHA-256とする。CatalogとSource Bundle IndexはともにSourceのindexed root外へ置き、CatalogをそのIndexのEntriesへ含めない。Source Bundle内のfileからCatalog／Manifest／Receiptへhashを逆参照させない。Catalog自身のhashはCatalogのcanonical bytes全体から外部で計算する。これにより循環hashなしでCatalogから母集合のSource Bundleを一意に固定する。

ReceiptからCatalog hash、CatalogからSourceBundleContentSha256、Indexから実treeの全file集合・長さ・hashを順に検証する。参照Entryのfileが一致しても、別Indexや余分なfileを持つBundleで代用しない。実treeの完全一致とは別に、固定adapterによるSource境界のmetadata列挙とCatalogの全Entry（Excludedを含む）の対応を検査し、依存画像等の補助fileをSource件数へ数えない。この完全性確認のためにTriangle計数・完全Geometry評価を追加しない。

Entry property順は`CatalogEntryOrdinal／SourceFixtureId／SourceProvider／SourceRelativePath／SourceFileByteLength／SourceFileSha256／SourceRootLocator／Category／BatchStratum／ShapeStratum／Phase02Eligibility／EligibilityRuleId／ScopeReason`とする。CatalogEntryOrdinalは配列位置と同じ`0..EntryCount-1`、SourceFixtureIdは`[A-Za-z0-9._-]{1,64}`、SourceProviderは`Synty／PolyProUniverse`だけ、Categoryは`Character／Building／Vehicle／Environment／Props`だけとする。SourceRelativePathはSourceBundleContentSha256で指定したIndexに対応するSource Bundle root相対のcanonical通常file path、SourceFileByteLengthは正かつ最大安全整数`9007199254740991`以下、SourceFileSha256はそのfileの全byte hashとし、Bundle Indexへ照合する。同じfile内のAssetは固定adapterが出す非空NFC文字列SourceRootLocator（最大1024 UTF-8 byte、control文字禁止）で区別し、`SourceProvider + SourceRelativePath + SourceRootLocator`の重複を拒否する。Asset境界、Category、Rule評価が不明ならCatalog Freezeを止め、未知値やTool失敗をExcludedへ偽装しない。

Catalog Freezeではfile identity、Source境界、Category、Eligibility、stratumだけを確定し、全FBX／全Source rootに対するTriangle計数・完全Geometry評価の追加passを要求しない。Source境界の列挙等に必要なmetadata読込は許可するが、Excludedを計数目的だけでImportしない。Propsおよび後述Building自動screeningのEligibilityに必要な最小preflight値は、Rule固有の判定記録としてSource Bundleに保持し、SourceFixtureId／Source file hash／適用Rule ID／必要な測定値と判定へ結合する。Propsの記録形式と測定項目はversion付きRule configuration、Buildingは後述のBuilding判定記録v1を正本とし、Catalogへ共通Triangle計数を追加しない。判定記録にCatalog／Source Bundleのhashを逆参照させない。Eligibility自体を判断不能な場合と、通常Import後のTriangle計数失敗を混同しない。

`SourceTriangleCount`はCatalog fieldではなく、Eligible Sourceの通常Import成功後に計数してReportのPlannedVariantEntryへ記録する。値は取得済みなら`0..9007199254740991`の整数、Import／計数完了前の失敗では明示nullとし、不明値を0へ置き換えない。Source規模は派生Recipe適用前の固定計数規則で測り、ActualInput／ActualOutputとは区別する。正の計数値からSource Triangle帯を求め、未取得または0なら帯はnullとする。Source規模上限とSource Triangle帯による選抜はこの通常処理以降に行い、batchの割当条件へ戻さない。Import／計数の制御可能な失敗は既存のToolFailed／ResourceDeferred等とStage／Reasonで当該予定Entryを終端し、残る依存Entryにも原因を残す。確定済みCatalog／Manifestを無効化したり、SourceをExcludedへ変更したり、他batchの開始を止めたりしない。

Phase02Eligibilityは`Eligible／Excluded`だけとする。EligibilityRuleIdはEntry単位で必須、rootにはRule SetのID／hashだけを置く。現行Rule Set IDは`phase02-eligibility-v3`とする。Rule Setはcanonical Preset文書で、property順`SchemaVersion／EligibilityRuleSetId／ScriptBundleContentSha256／RuleCount／Rules`、SchemaVersion `1`、RuleCount `5`とする。Rulesは下表のCategory順に5件、property順`EligibilityRuleId／PredicateRelativePath／PredicateContentSha256／ConfigurationRelativePath／ConfigurationContentSha256`とする。predicateはScript Bundle、configurationはPreset Bundleへのcanonical相対pathと正確なfile hashを持ち、固定family／preflight形状条件／Building自動screening規則を含む。Catalog rootのRule Set hashはこの文書のcanonical bytesへ照合する。ConfigurationはRule Set／Catalog／Preset Bundle hashを逆参照せず、循環hashを作らない。表の語彙・優先順を変更する場合はRule Setと必要なSchema versionを更新し、同じIDの意味を差し替えない。

| Category／EligibilityRuleId | 順に評価する条件 | Phase02Eligibility | ScopeReason |
| --- | --- | --- | --- |
| Character／`phase02-character-v1` | SourceProviderがSynty | Excluded | `SyntyCharacterOutOfScope` |
| 同上 | SourceProviderがPolyProUniverse | Eligible | null |
| Building／`phase02-building-v2` | SourceProviderがSynty | Excluded | `NonPolyProBuilding` |
| 同上 | PolyProUniverseでBuilding本体family外、または後述screeningの正常な不合格 | Excluded | `NonBoxLikeBuilding` |
| 同上 | PolyProUniverseでBuilding本体family内かつ後述screeningの全条件に合格 | Eligible | null |
| Vehicle／`phase02-vehicle-v1` | いずれかの許可SourceProvider | Eligible | null |
| Environment／`phase02-environment-v1` | SourceProviderがSynty | Excluded | `NonPolyProEnvironment` |
| 同上 | PolyProUniverseでTree／Rockのいずれでもない | Excluded | `NonTreeOrRockEnvironment` |
| 同上 | PolyProUniverseでTreeまたはRock | Eligible | null |
| Props／`phase02-props-v1` | SyntyのSM_Prop_*またはPPU Propsという固定family条件の外 | Excluded | `NonPropSourceFamily` |
| 同上 | family内で固定family＋preflight形状条件により床貼付の非破壊板 | Excluded | `FloorAttachedNonDestructiblePlate` |
| 同上 | family内で上記除外に該当しない | Eligible | null |

ScopeReasonの非null値は表の7値だけとし、Eligibleは必ずnull、Excludedは必ず対応する非null値を持つ。CategoryとRule IDの不一致、表にないRule × Eligibility × ScopeReason、providerと矛盾する結果を拒否する。Rule評価は生成結果を見る前に完了させる。

旧`phase02-building-v1`／`phase02-eligibility-v2`は人間判定版の履歴として保持し、自動screening版へ暗黙変換しない。今回のRule更新でCatalog v2のproperty構成・ScopeReason語彙、Rule Set文書SchemaVersion 1は変更しない。

**Building自動screening（phase02-building-v2）。** PPUの元の`Assets.blend`から正式adapterが決定論的に発見するroot＋descendantを測定対象とする。Building本体familyはversion付きの固定名前／collection規則だけで判定し、意味解析、学習型分類器、pilotの176件のassembly再構築を要求しない。family外は形状測定を行わずExcluded／NonBoxLikeBuildingとする。family内はZ方向BBox Coverage `>= 96%`、4方向Depth Medianの最大 `< 1 m`、水平BBox短辺 `>= 2 m`、高さ `>= 2 m`をすべて満たす場合だけEligible／nullとし、正常に測定できた不合格はExcluded／NonBoxLikeBuildingとする。219件は今回の入力観測件数でありSchemaの固定件数ではない。

測定前にversion付きPresetと本隊Scriptへ、対象descendant／評価Geometryの包含規則、transform・scale・meter換算、Z方向を含む測定座標系、BBox軸、4方向とDepthの起点・距離定義、sampling配置・件数・上限、rayのhit規則、数値精度を固定する。閾値の比較演算は上記どおりとし、表示用丸めで採否を変えない。Coverageは全sampleを分母、hit数を分子とする。Depthの部分未命中はMedian対象から除き、ある方向の全件未命中は正常な不合格とする。偶数件Medianは中央2値の算術平均とし、中間値を含む非有限値を不合格値へ丸め込まない。BVH構築失敗、測定不能、非有限値、測定上限超過、Source境界不明は判定不能としてCatalog Freeze全体を失敗させ、Excludedへ自動変換しない。family除外等で実行不要な測定と全件未命中によるMedian不在は、判定不能とは区別してRule固有記録へ明示する。

Building preflightはCatalog Freeze前の限定Geometry測定として許可し、全SourceのTriangle計数禁止を解除しない。固定Blender `4.5.12`の単一読み取り専用processで元blendを一度開き、Sourceを固定順に測定してSourceごとのBVH／一時評価資源を解放する。Sourceごとのprocess isolation、Depth Wall／Surface Fill／Decimate／Bake等の生成Recipe、画像生成、閉鎖性証明を要求しない。元Source fileを保存・変更せず、pilotコード・生成Geometry・assembly・測定結果を正式成果物へ直接採用しない。必要な測定処理だけを本隊Script Bundleへ最小再実装する。軽量性はpilotで確認済みとして扱い、人日・秒数見積りを新しい性能SLAや採用Gateにしない。

**Building判定記録v1。** Source Bundle内の固定相対path `phase02-preflight/building-v1.json`を、当該Bundleの唯一の正式Building判定記録集合とする。Source Indexへ通常fileとして長さ／hashを収録し、この補助fileをadapterのSource列挙へ含めない。CatalogをFreezeする前、およびFrozen Catalogを再利用・Receipt検証する際に必ず照合する。対象集合はCatalog Entryのうち`SourceProvider=PolyProUniverseかつCategory=Building`の全Sourceであり、family外、Excluded、未割当、frontier外を含む。対象Sourceごとに厳密1 record、対象集合とRecordsのSourceFixtureId集合は完全一致とし、欠落・重複・余分なrecord、別Source identity、別Ruleのrecordを拒否する。Props等の別種判定記録をBuildingの余分recordとは数えない。別pathの古いBuilding集合を代替証拠として選択・mergeせず、旧記録・途中結果・失敗診断はFrozen Source Bundle外の作業領域に置く。

root property順は`SchemaVersion／EligibilityRuleId／ScriptBundleContentSha256／PredicateContentSha256／ConfigurationContentSha256／BlenderVersion／BlenderExecutableSha256／RecordCount／Records`とする。SchemaVersionはinteger `1`、EligibilityRuleIdは`phase02-building-v2`、BlenderVersionは`4.5.12`、各hashは小文字64桁SHA-256とする。Script Bundle／predicate／configuration hashをCatalogのRule Set文書の該当Ruleと正確に照合し、Blender executable hashは既存の固定Blender Manifestと照合する。記録自身、Catalog、Source Bundle Indexのhashを記録へ埋め込まず循環参照を作らない。RecordCountは`0..100000`で実配列長と一致し、RecordsはSourceFixtureIdのordinal昇順かつ一意とする。対象Sourceが0なら空配列の文書を持つ。

record property順は`SourceFixtureId／SourceRelativePath／SourceFileByteLength／SourceFileSha256／SourceRootLocator／MeasurementStatus／FamilyMatched／CoverageSampleCount／CoverageHitCount／HorizontalBBoxSizeMeters／HeightMeters／Directions／Phase02Eligibility／ScopeReason`とする。Source identity fieldの型・上限はCatalog Entryを継承し、pathは最大1024 UTF-8 byteに制限する。全identity fieldを対応Catalog EntryおよびSource Indexへ照合する。各recordはrootのRule ID／実装hash／設定hash／Blender identityを継承し、対応Catalog EntryのEligibilityRuleIdもrootと一致させる。MeasurementStatusは`FamilyExcluded／Measured`の2値、FamilyMatchedはJSON booleanとする。`FamilyExcluded`はFamilyMatched=false、Coverage両count／HorizontalBBoxSizeMeters／HeightMetersがnull、Directionsが空配列、Phase02Eligibility=Excluded、ScopeReason=NonBoxLikeBuildingだけを許可する。

`Measured`はFamilyMatched=true、CoverageSampleCountが`1..1000000`、CoverageHitCountが`0..CoverageSampleCount`、HorizontalBBoxSizeMetersが非負finite binary64の厳密2要素、HeightMetersが非負finite binary64とする。DirectionsはPresetの固定4方向順に厳密4要素、各要素のproperty順を`DirectionOrdinal／SampleCount／HitCount／MedianDepthMeters`へ固定する。DirectionOrdinalは配列位置と同じ0..3、SampleCountは1..1000000、HitCountは0..SampleCount、MedianDepthMetersはHitCount=0なら必ずnull、それ以外は非負finite binary64とする。全sample数はPresetが当該Sourceへ定めたsampling規則・上限とも一致させる。family内は正常に測定できた4方向すべてを記録し、不合格を理由に途中の方向を省略しない。全件未命中はMeasuredの正常な不合格であり、判定不能ではない。

Loaderは保存値から、Coverageの`25 * CoverageHitCount >= 24 * CoverageSampleCount`（checked整数演算）、4方向すべてHitCount>0かつMedianDepthMetersの最大<1、水平2寸法の最小>=2、HeightMeters>=2を検査する。全成立だけEligible／null、その他はExcluded／NonBoxLikeBuildingとし、recordのPhase02Eligibility／ScopeReasonおよびCatalog Entryの同値と厳密に一致させる。family判定と測定そのものは固定predicateの実行記録を信頼し、照合のためにBlender／BVHを再実行しない。BVH構築失敗・非有限・上限超過・境界不明等の判定不能はこの正常記録集合へ収録せず、診断を作業領域へ残してCatalog Freezeを停止する。`Measured`やExcludedへ偽装した失敗recordを受理しない。

encodingは本節のcanonical UTF-8 JSON共通規則を使い、固定property順・省略禁止・明示nullに加え、未知／欠落／重複property、未知enum、末尾dataを拒否し、canonical再serializeとbytes完全一致を要求する。integerは先頭0なしの最短10進表記、測定数値はfinite binary64の不変Culture・最短round-trip JSON numberとし、負の0は0へ正規化する。文字列はNFC、quote／backslashだけを必要なJSON escapeで表し、control文字を禁止する。文書最大64 MiB、record最大16 KiB、Records最大100000件、水平寸法最大2要素、Directions最大4要素であり、ray sample配列や任意の測定payloadを追加しない。Loaderはschema上限以下の呼出側byte／record件数上限を必須とし、全量buffer／配列確保前に上限を検査する。非seek入力はlimit+1までの試読で超過を拒否する。新しいReceipt、journal、Sourceごとのfile生成を要求しない。

固定入力・実装・Preset・Blender identityが一致する完了recordは照合して再利用でき、batchごとの再測定を要求しない。不一致時は影響Sourceだけ再測定し、今回の対象集合に一致する正式記録集合を組み立てる。変更したRule Setと選抜集合には新しいCatalog／Manifest／Selection RunまたはDataset revisionを用い、旧Frozen集合を上書きしない。依存が変わらない生成GeometryとReview記録の再利用は既存のEntry単位provenance規則に従い、全体再生成を要求しない。

このscreeningは矩形footprint候補の近似選抜であり、4面閉鎖やStructuralSlabInput成立の証明ではない。偽陰性と開始前の測定コスト、1件の判定不能によるFreeze停止を許容する。Human Reviewは規則と合格・不合格双方の少数sample監査に限定し、個別Sourceのmembershipを直接変更しない。正式root＋descendantとpilot assemblyの判定件数・結果一致は要求しない。偽陽性は後続Geometry／Role GateでRejectedとして扱い、Catalogを遡及Excludedへ変更しない。各facade閉鎖性、corner contact、4 Slab以上のStructuralSlabFixture等の下流条件は維持する。quota不足時は既存どおり人間へ戻し、個別例外で埋めない。

T-078／T-081は少数の合成FixtureとGoldenで、閾値一致（Coverage 96%・寸法2 mは合格側、Depth 1 mは不合格側）、部分／全件未命中、偶数Median、family外の測定非実行、BVH失敗／非有限／上限超過によるFreeze停止、記録とRule／Source identity照合、旧Rule意味の差し替え拒否を確認する。単一processでの順次処理・BVH解放と完了記録再利用を確認し、全219件の繰返し実行、全Source隔離process、性能比較matrixを必須試験へ追加しない。 Building判定記録v1は少数Goldenで、family外を含む対象集合の厳密1対1、欠落／重複／余分record、source path／root／file／Rule実装・設定hashの不一致、保存測定値→record判定→Catalog判定・理由の不一致、失敗診断混入、version／property／null違反、byte／配列上限超過を拒否することを確認する。Props記録をBuilding集合へ混同せず、Blender再実行なしの照合で検査する。

BatchStratumはEligible Character／Building／Vehicleで`General`、Eligible Environmentで`Tree／Rock`、Eligible Propsでprovider対応の`Synty／PPU`とする。Excludedはnull。ShapeStratumはEligible Propsだけが`Plate／Slender／Cuboid／Other`のいずれかを持ち、他はnullとする。ShapeStratumは多様性quota用で、Propsのbatch消費軸はあくまでproviderである。これらの値はFreeze前の固定adapter／Rule Setで確定し、batch作成時に再分類しない。全EntryはCatalogEntryOrdinalを持つが、Catalog EntryにBatchOrdinal／BatchSourceOrdinalを持たせない。

**Batch Manifest。** `EarlyFixtureBatchManifest` v1のroot property順は`SchemaVersion／BatchId／BatchOrdinal／BatchKind／CatalogContentSha256／BatchAllocationProfileContentSha256／SourceCount／Sources`とする。SchemaVersionは`1`、BatchKindは`Pilot／Expansion`だけ。BatchOrdinalは`0..99999`で連続し、0だけがPilot、1以降がExpansion。SourceCountはPilot `12`／Expansion `6`に固定する。BatchAllocationProfileContentSha256はCatalogの同名fieldと一致する。BatchIdは小文字Catalog hash、`.`、小文字BatchAllocationProfileContentSha256、`.batch.`、先頭zeroなし十進BatchOrdinalをこの順に連結する（最大141文字）。同じ割当入力では同じIDとなる。

SourcesのEntry property順は`BatchSourceOrdinal／SourceFixtureId／Category／BatchStratum`とする。BatchSourceOrdinalは配列位置と同じ`0..SourceCount-1`。SourceFixtureId、Category、BatchStratumはCatalogの同じEligible Entryと完全一致させる。Source列はCategory順`Character → Building → Vehicle → Environment → Props`、Environment内は`Tree → Rock`、Props内は`Synty → PPU`、同じstratum内はSourceFixtureId ordinal順とする。Manifest内および同じ全割当集合内のSource重複を拒否する。Excludedまたは未割当SourceはどのManifestにも存在せず、BatchOrdinal／BatchSourceOrdinalを持たない。一方、割当済み・未実行Sourceの両ordinalはManifest上で維持する。

Manifest content hashはcanonical bytes全体のSHA-256として外部の参照record／Receiptに`BatchManifestContentSha256`名で記録し、Manifest自身には持たせない。参照recordのproperty順は`BatchId／BatchOrdinal／BatchManifestRelativePath／BatchManifestContentSha256`、相対pathは割当root内の`batch-<BatchOrdinal>.json`へ固定する。ordinal部分は先頭zeroなし。別fileから同じIDを代用したり、再起動後に読込順から割当を推測したりしない。

**一括割当。** Catalog Freezeでは、EarlyFixtureBatchAllocationProfile v1のcanonical bytesだけを割当Profileとしてhash固定し、Eligibleだけの次の7 queueをSourceFixtureId ordinal順に構築する：Character-General、Building-General、Vehicle-General、Environment-Tree、Environment-Rock、Props-Synty、Props-PPU。Pilotはそれぞれ`2／2／2／1／1／2／2`件を消費する。ExpansionはCharacter／Building／Vehicleから各1、Propsの各queueから1ずつ、EnvironmentはBatchOrdinalが奇数ならTreeから1、偶数ならRockから1を消費する。各batchは必要queueすべての残数を先に確認してから一括消費する。1つでも不足すればそのbatchを作らず、他queueも消費せず、以降の均衡batch生成を終了する。別stratumでの穴埋めや後続の作りやすいbatchへのskipは禁止する。Pilotも作れなければ割当Freeze未成立とし、正式実行を開始しない。

構成可能な全batchをこの規則で先に列挙し、全EarlyFixtureBatchManifestを一括確定してからCatalog Freezeを成立させる。これは全Geometry生成を先行実行する要求ではない。固定Catalog／割当Profileから期待Manifest列を再計算し、全fileのcanonical bytes／外部hash／件数／順序／Source一意性を照合することで完備性を検証する。作成中断で一部fileだけがある場合は実行を開始せず、期待bytesと一致するfileを再利用して欠けたManifestだけを作る。不一致fileは上書きせず停止する。新しいjournalやGeometry再生成を必要としない。

実行中は既存Manifestを順に消費するだけとし、結果・所要時間・ReviewからSourceを移動しない。後の処理／評価Profile改訂はEntry単位provenanceへ記録し、Manifestの割当Profile hashを書き換えない。Source母集合・Eligibility・batch構成規則を変える場合だけ新Catalog／割当Profile identityで全Manifestを別割当rootへ確定する。既存Geometryの再利用可否はその生成provenanceと新しい用途Gateで判断し、割当改訂だけで全再生成を要求しない。

**最終Reportの被覆。** Receiptの`LastIncludedBatchOrdinal`は実在する`0..N-1`のいずれかであり、対象Manifest参照列は必ず`0..LastIncludedBatchOrdinal`の完全なprefixとする。最終Reportはこのprefix内の全Sourceと、各Sourceについて実行前にversion付きPresetから確定した全PlannedVariantEntryを過不足なく含む。Source集合はReport EntryのSourceFixtureIdの重複排除集合として検証し、同じSourceに複数Variantがあることは正常とする。予定Variantのキー／順序／Recipe／生成・評価Profileを実行記録へ残し、終端結果から予定集合を逆算しない。

Excluded、未割当、frontier外の割当済みSource（未実行・実行中・完了済みを含む）は最終Report Entryを持たない。frontier外のAttempt／結果は再開用記録・監査資料へ保持してよいが、このReport／Dataset採用／Receipt対象へ混ぜない。frontier内の全予定Entryは終端5値のいずれかを持ち、成功だけを抜き出して被覆を満たしたことにしない。Catalog全数・Eligible／Excluded／未割当／frontier外の件数は別の集計として保持し、Report Entryを水増ししない。

Catalogは16 MiB／100000 Entry、Rule Setは64 KiB／厳密5 Rule、各Batch Manifestは64 KiB／最大12 Source、全Manifest参照は100000件以下とする。各Loaderの呼出側byte／件数上限、checked整数演算、参照先path／hash検証を必須とする。T-081のGolden／negativeはproperty順、全enum／null／許可表、Rule Set hash、7 queueの枯渇、重複Source、全一括割当の欠落／追加／再開、ordinalの3種区別、Manifest自己hash禁止、Report prefixの欠落／余分Source／非終端Variantを含む。T-078では一括割当済みManifestから処理を再開しても同じ順序・同じfrontier採用になることを確認する。 CatalogへのSourceTriangleCount混入、SourceFixtureIdの64文字受理／65文字拒否、Manifestの旧ProfileContentSha256名を拒否するGoldenも追加する。計数を要求しないCatalog Freeze、Excludedの計数非実行、通常Import／計数失敗のnull統計とEntry終端、他Sourceの継続をfakeと少数Fixtureで検証し、全Catalogの追加Import試験は作らない。CatalogのSourceBundleContentSha256欠落／不一致、参照fileだけ同じで余分なSourceを含む別Bundleへの差し替え、実treeへの余分file追加、CatalogをSource Index内へ含める循環配置を拒否する。Receiptから正確なBundleまで検証できることを小さいfile-tree Fixtureで確認する。

##### 最低成果物・再現性

| Group | 最低採用unique Source数 | 構成 |
| --- | ---: | --- |
| Character | 2 | 異なるBody topology、うち1件以上cap候補 |
| Building | 2 | `phase02-building-v2`でEligibleとなったPPU Source。少なくとも1 SourceはSurfaceFill OFF／ON比較を含み、2 Sourceとも既定Geometry／Role Gateを通過した4 Slab以上のSelected StructuralSlabInputを持つ。「豆腐型」等の追加の人間形状認定を要求しない |
| Vehicle | 2 | 小型／大型または異なるException hierarchy |
| Environment | 2 | Tree1／Rock1 |
| Props | 4 | Synty2／PPU2、複数shape stratum |
| 合計 | 12 | 各GroupにSelected RenderCutInputを最低1件 |

Licensed RenderCutInputは実Triangle帯`1..224／225..707／708..1414／1415..3162／3163..7071／7072..Profile上限`の6帯中5帯以上を満たす。性能境界の不足帯をBenchmarkOnlyで補えるが、Blind由来だけでカテゴリquotaを満たさない。Licensed PhysicsCookInputはBuilding／Vehicle／Propsから合計6 unique Source以上を要求する。NoOp／Alias／複数BindingをSource数として重複加算しない。quota未達は未完了として人間へ戻し、自動緩和しない。Freeze時の実数はDataset Index／ReceiptとPhase完了記録へ残す。

正式Licensed生成は原則1回とし、各Category最低1 Source、EnvironmentはTree／Rock両方（合計最低6 Source）のhash-seeded固定監査sampleだけをclean directoryへGeometry／sidecarと対応Entryを再生成し、Review PresentationのUV／atlas／bake／画像は対象外とする。選抜seed／規則と数値toleranceは監査前に固定し、上記Licensed再現性監査の初期toleranceと論理親参照の比較規則を使う。ProcessStatus、Variant graph、SelectionClass、Transform、Bounds、Triangle／Component、surface deviation等の用途上の意味的等価を検証し、LicensedのBlender出力や再生成ZCGのbyte完全一致は要求しない。ただし採用済みfileのcontent hash照合、同一ZCG入力のcanonical再serialize一致は常に要求する。差異は影響cohortだけを再検証する。公開Synthetic／Goldenは2回のclean生成でbyte一致を要求する。

公開Synthetic SuiteはPositive TriangleMesh 8 case（SingleCapLoop、MultipleCapLoop、Concave、MultipleShell、CenterCut、EdgeCut、NonIntersectingCut、3個以上の閉Componentを含む既知の入力形状）とNegative 4 case（反転winding、退化、開放Boundary、Non-Manifold Edge）を固定Case IDで持つ。切断caseは入力Geometry／Cut Plane／期待交差・入力の幾何Component数・Cap Loop・退化分類を渡すだけで、Phase 0.2でcutterや切断済み出力を実装しない。3個以上の閉Componentを含む入力caseは既知順序／OBBを持つ4面Synthetic StructuralSlabFixtureを兼ねる。NegativeはPassed Watertight Datasetへ入れず別のtest fixture群とする。

Synthetic Convexは単一Hullの4／16／64／128頂点をCook Positive、129頂点をZCG成功・PhysicsCookInput拒否のRole Negative、255頂点をBindingなしのCodec Positiveとする。3／256頂点、HullCountが1以外をCodec Negativeへ固定する。Face／index導出上限の直前・一致・超過を検査するが、独立Face budgetやCompound規模系列を成果物へ追加しない。既存のSynthetic Solid Numeric Kernel／Validatorは次節のまま使用する。

##### canonical成果物・provenance・採用境界

新しい選抜modelは`EarlyFixtureSelectionProfile／EarlyFixtureSourceCatalog／EarlyFixtureSelectionReport／LicensedRepresentativeDatasetIndex／LicensedFixtureSelectionReceipt`のschema v2とする。旧v1 property／enumを同じversionで再解釈しない。既存v1 artifactが存在する場合も正式v2 Datasetへ暗黙変換・採用せず、旧Loader／Goldenがある場合はその意味を保存する。ZCG v1、CanonicalBundleIndex v1、Synthetic Watertight Validator v1のbyte形式は維持し、新しいGeometry Artifact／Fixture Binding／StructuralSlabFixture／EarlyFixtureEligibilityRuleSet／EarlyFixtureBatchManifest／EarlyFixtureBatchAllocationProfile／EarlyFixtureResourceProfileのcanonical schemaは初期v1とする。

Phase 0.2の最初の実装成果物として、各schemaの全property順、discriminated field有無、enum、ID／path規則、nullable、countと配列順をCodec／Goldenへ固定してから正式生成する。JSONはBOMなしUTF-8、余分な空白・末尾改行なし、固定property順、未知property／enum拒否、有限値の最短round-trip表現、小文字64桁SHA-256とし、非該当fieldは各schemaの明示nullまたはtagによる禁止のいずれか一方へ固定する。Schema確定を後続Phaseへ先送りしない。

Source／Script／Presetは`CanonicalBundleIndex` v1（`SchemaVersion／BundleKind／EntryCount／Entries`、Entryは`RelativePath／ByteLength／ContentSha256`）で識別する。通常fileを正規化相対pathのordinal順に列挙し、NFC、`/` separator、非空segment、absolute／dot／dotdot／control／backslash禁止、case-fold衝突禁止、symlink／junction／reparse point禁止を適用する。Index自身は対象rootの外に置く。実treeのfile集合、長さ、hashをIndexと完全照合する。生成・評価Presetは対応Bundleで固定する。Source CatalogはSourceのindexed root外の独立canonical artifactとし、ReceiptのCatalog hashで固定する。CatalogのSourceBundleContentSha256が指定するSource Indexを完全照合し、CatalogをSource Bundleへ内包する旧配置は使用しない。

各Geometry Artifact／sidecarと予定Entryは、Source／Source Geometry hash、固定Blender版／実行file hash、Script／Preset Bundle hash、生成Profile hash、RecipeVersion、親ID／hashを保持する。生成Entry.Provenanceは予定したResourceProfileContentSha256、各Attemptは実際に使用した同じResourceProfileContentSha256、評価Entryは使用したSelection／監査Profile hashを別に保持し、Catalog／ManifestのAllocation Profile hashへ混ぜない。単一のroot Profile hashで異なるcohortを上書きしない。各batch開始前とReceipt確定前に参照したimmutable Bundle versionの実treeを再検証し、driftした参照のままFreezeしない。新Bundle追加は旧Bundle driftと区別する。

Reportはfrontier内の全予定処理とAttempt、Catalog総数／Eligible／理由別Excluded／未割当／frontier外数、cohort、出力tag／参照、選抜結果を監査可能に保持する。Dataset Indexは選抜Binding、参照Geometry Artifact／sidecar、再検証に必要な親参照closure、各Format／Version／path／length／hash／provenanceを保持する。支持用Unselected親は測定対象Bindingと区別し、失敗Entryを採用しない。sidecarからGeometry、SlabBoxからsidecarへの参照も完全照合する。全GeometryはZCG v1、sidecarは固定JSONであり、Indexは正確なfile許可リストになる。Source／pilot fileやReview画像は測定Datasetへ紛れ込ませない。

`DatasetContentSha256`はcanonical Dataset Index bytesのSHA-256とする。観測時間／Peak Working Set／Report hashをIndexへ含めない。ReceiptはSelectionRunId、DatasetId、Report hash、Dataset Index hash（DatasetContentSha256と一致）、Catalog hash、LastIncludedBatchOrdinal、対象Batch Manifest hash列、採用実数へ結合する。Report／Index／Bundle／参照closure／quota／監査の検証後に最後に原子的確定する。Bundle検証はCatalogのSourceBundleContentSha256に結合した正確なSource Indexを対象とし、Receiptからこの参照鎖を省略しない。Receipt欠落、不一致、frontier内の非終端Entry、重大監査IssueがあるRunは下流へ渡さない。後続ManifestはDataset ID／hashと正確なFixture Bindingを指定し、Render／Physics／構造用途をファイル名から推測しない。

実施順は、baseline／最小移植→canonical schema→ZCG／Numeric Kernel→Catalog／Bundle→共通Import／Gate→カテゴリRecipe／単純Convex→Synthetic Suite→batch orchestration→小Pilot→均衡拡大／監査→少数sample再現性監査→frontier／Receipt確定→下流引渡しとする。公開Synthetic作業はCodec確定後にLicensedカテゴリ処理と並行できる。具体的なBlender画面操作、Review view数、作業task番号、pilot成功件数、使い捨てextension導入手順は実施計画に残し、DESIGNの完了条件へ昇格しない。

##### Synthetic Watertight Dataset専用Validator

閉形状既知正解用の`SyntheticWatertightFixtureProfile`、`SyntheticWatertightDatasetIndex`、`SyntheticFixtureValidationResult`はLicensed schemaと別version／別content hashを持ち、本節のcanonical UTF-8 JSON共通規則を再利用する。Profile v1のproperty順は`SchemaVersion`、`ProfileId`、`AbsoluteEpsilonMeters`、`RelativeEpsilon`、`MaxTriangleCount`、`MaxConnectedComponentCount`、`MaxSelfIntersectionCount`、`SelfIntersectionAlgorithm`、`MaxCandidatePairCount`とし、値をそれぞれinteger `1`、`synthetic-watertight-v1`、`0.000001`、`0.000001`、`200000`、`256`、`0`、`ClosedTriangleDistanceV1`、`2000000`へ固定する。

Dataset Index v1のproperty順は`SchemaVersion`、`DatasetId`、`ProfileContentSha256`、`ScriptBundleContentSha256`、`CaseCount`、`Cases`とする。各Caseは`DatasetCaseId`、`GeneratorId`、`GeneratorRecipeContentSha256`、`GeometryRelativePath`、`GeometryByteLength`、`GeometryContentSha256`、`ValidationResultContentSha256`の順とし、DatasetCaseId ordinal順、1..100000件、一意な正規化`.zcg` path、16..67108864 byte、各小文字64桁hashを要求する。Generator RecipeはGenerator引数を含むcanonical JSON fileとしてScript Bundleへ収録し、そのbytesをhashする。Validation Result v1は`SchemaVersion`、`DatasetCaseId`、`ProfileContentSha256`、`GeometryContentSha256`、`TriangleCount`、`ConnectedComponentCount`、`BoundaryEdgeCount`、`NonManifoldEdgeCount`、`OrientationMismatchEdgeCount`、`SelfIntersectionCandidatePairCount`、`SelfIntersectionCount`、`TotalSignedVolume`、`Passed`、`FailureReason`の順とする。`TriangleCount`は非負integerとし、後続Count／Volumeは当該Gateへ到達前に失敗した場合だけ`null`、到達した場合は非負integer／finite numberとする。Passedでは全統計を非null、TotalSignedVolumeを正のfinite値とする。FailureReasonは`None`／`NonFinite`／`Degenerate`／`Boundary`／`NonManifold`／`Orientation`／`NonPositiveVolume`／`SelfIntersection`／`CandidatePairLimit`／`InputLimit`／`ValidatorUnavailable`とし、Passedなら`None`、不合格なら非Noneを必須とする。

Profile／Validation Resultは64 KiB、Dataset Indexは100000 Case／64 MiBをschema上限とし、Loaderへ同値以下の呼出側byte／件数上限と配列確保前検査を必須とする。Indexへ入れるCaseはPassedだけとし、不合格ResultはGenerator Runの診断Bundleへ保持する。Licensed SourceFixtureId、旧Tier／GeometryProcessMode／QualityClass、Licensed Reportの選抜Statusを使用しない。公開Synthetic Convex／Cut期待値／StructuralSlabFixtureのSuite Indexは別の初期schemaとして10.2.2のArtifact／Bindingを参照し、Watertight TriangleMesh専用IndexへConvexやNegativeを混在させない。

以下の`SolidSignedVolumeV1`、`SolidGeometryValidatorV1`、`SolidCandidateBvhV1`、`ClosedTriangleDistanceV1`という互換名はSynthetic Watertight Datasetのテスト用Validatorだけを指す。製品Strict Solid、Licensed Solid Tier、製品Preprocessor成果物を意味せず、Licensed Harnessから呼び出さない。Synthetic側でProfile上限または形状Gateに失敗したcaseは`SyntheticFixtureValidationResult`を不合格としてDataset Indexへ採用せず、Licensed Reportの`Rejected`／`ProfileUnsupported`へ変換しない。

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

検証対象domainのbinary32 positionから各軸min／maxをpositionのcanonical順に比較して求め、軸差を`dx,dy,dz`とする。`D = sqrt(((dx*dx + dy*dy) + dz*dz))`、`epsDistance = max(Profile.AbsoluteEpsilonMeters, D * Profile.RelativeEpsilon)`、`epsArea = epsDistance * epsDistance`、`epsVolume = epsArea * epsDistance`とする。Dが非正／非有限ならRejectする。距離／半空間誤差はepsilon以下を包含側とする一方、非退化面積と正体積はそれぞれ`> epsArea`、`> epsVolume`を必須とし、等号は退化側としてRejectする。 ただし、後述の`SharedSimplexIntersectionV1`のnarrow phaseだけは距離／半空間のepsilon包含規則の例外とする。先行する退化Gate、BVH候補生成、非共有pairの距離判定はこの例外に含めない。

`TriangleMesh` payloadは`uint32 PositionCount`、`uint32 TriangleCount`、続いてPositionCount件の`float32 x,y,z`、TriangleCount件の`uint32 i0,i1,i2`とする。元Geometryを位置だけのtriangle soupへ展開し、完全に同じ正規化positionを1件へweldして、positionを数値`x,y,z`のlexicographic昇順へ並べ直す。各Triangleは新indexへremapし、windingを反転せず3 indexをcyclic rotationして辞書順最小表現にし、Triangle列全体を`i0,i1,i2`の辞書順へsortする。範囲外index、同一頂点を含むTriangle、同一index tripleの重複をRejectする。Licensed TriangleMeshとSynthetic Watertight TriangleMeshはこのKindを使う。

Triangle退化判定のdomainはTriangleMesh全体とし、上記domain Boundsからepsilonを1回だけ計算する。各Triangleについて`u=v1-v0`、`w=v2-v0`、`twiceArea = length(crossRH(u,w))`を記載順binary64で計算し、`twiceArea > epsArea`だけを合格とする。`twiceArea == epsArea`とそれ未満はRejectし、binary32 positionの1 ULP差で境界をまたぐ場合もこの比較結果をそのまま使用する。実面積へ0.5を掛けてから比較したり、TriangleごとのBounds、Blender double、Unity float、近似Normal長を使ってはならない。

座標変換のGolden Fixtureは`M_root=identity`、`M_object=translation(10,20,30)`、`s=0.5`、Blender local triangle `[(1,2,3),(4,6,5),(-2,7,11)]`を入力とする。ZCG変換、position sort、triangle cyclic rotation後はpositions `[(4,20.5,13.5),(5.5,16.5,11),(7,17.5,13)]`、triangle `[0,1,2]`、payload length 56、file length 72でなければならない。完成fileのhexは`5a4347310100000038000000000000000300000001000000000080400000a441000058410000b04000008441000030410000e04000008c4100005041000000000100000002000000`、SHA-256は`5210748ea4fe7a8f349b52e919af7dd1aad4c542a91fb741806bf517f2426cdbf`へ固定する。

`ConvexSet` payloadは`uint32 HullCount`の後にHull recordを連結する。各Hull recordは`uint32 PositionCount`、`uint32 FaceCount`、position列、各Faceの`uint32 IndexCount`とindex列からなる。Hull内positionはTriangleMeshと同じ規則でweld／sort／remapする。Face loopは外向きwindingを維持したままcyclic rotationで辞書順最小化し、Face列をIndexCountとindex列の辞書順へsortする。各Hullを一時canonical bytesへserializeし、そのbytesのunsigned byte lexicographic昇順でHull recordをsortする。単純Convex ArtifactはこのKindを使う。

Convexの検証domainはHullごととし、各Hullのbinary32 position Boundsから`ZcgNumericKernelV1`で`epsDistance`／`epsArea`／`epsVolume`を独立に計算する。比較境界と演算精度は共通Kernelから変更しない。

各Faceはcanonical rotation後の`v0`を固定し、`i=1..IndexCount-2`の順に`c = -crossRH(v[i]-v0, v[i+1]-v0)`を計算して、`length(c) > epsArea`となる最初のtripletをPlane生成へ使う。存在しなければFaceを退化としてRejectする。`n = c / length(c)`、`d = -(((n.x*v0.x + n.y*v0.y) + n.z*v0.z))`とし、このPlaneをそのpolygon faceの唯一の解釈とする。Face全頂点で`abs((((n.x*v.x + n.y*v.y) + n.z*v.z) + d)) <= epsDistance`を要求し、非平面polygonをepsilon内だけ許可する。Hull全頂点について同じ値が`<= epsDistance`であることを要求し、1点でも正側へ超過したHullを非凸または内向きFaceとしてRejectする。

Topologyは各FaceのIndexCount 3以上、範囲内でFace内重複indexなし、重複Faceなしを要求し、各undirected edgeがちょうど2 Faceに現れてdirected向きが互いに逆であることを閉鎖条件とする。Hull bounds centerを`r`とし、canonical Face順と各Faceのfan順で`V = left_sum(-dot(v0-r, crossRH(v[i]-r, v[i+1]-r)) / 6)`を計算する。ZCGのclockwise外向き規約では`V > epsVolume`を必須とし、`V <= epsVolume`、負volume、非有限volumeをRejectする。Face半空間、閉鎖edge、正volumeの全条件を通ったものだけをConvexとして扱う。3未満のFace indexもRejectする。Phase 0.2ではHullCountを1、頂点数を4..255へ制限し、FaceCount <= 2V - 4、全FaceのIndexCount合計 <= 6V - 12をchecked算術で割当前に検査する。独立Face上限は設けず、PhysicsCookInputの128頂点Role GateをCodecと分離する。

ZCG Encoderは同じ正規化Geometryから常に同じbytesを生成し、`GeometryContentSha256`は完成ZCG file bytes全体のSHA-256とする。Alias判定も同じSourceFixtureId＋GeometryKind内のこのhashと、10.2.2の先行Artifact順で行う。Verifier／Benchmark LoaderはIndexのFormat／VersionでDecoderを選び、decode後に同じEncoderで再serializeしたbytesが入力fileとbyte-for-byte一致しなければnon-canonicalとしてRejectする。これにより元Triangle／Vertex／Hullの列挙順、FBX metadata、container timestampはGeometry hashへ影響せず、位置、winding、Topologyの変化だけがcanonical bytesへ反映される。

全VariantはZCG encode後にfileをDecoderで読み直し、decodeされたbinary32 positionとcanonical indexだけを入力として最終Gateを再実行する。Licensed TriangleMeshはfinite、Bounds、Triangle退化／重複、Early Licensed ProfileのTriangle／Component上限を再検証し、Fixture Bindingには別途10.2.2の用途別Gateを適用する。別DatasetのSynthetic Watertight FixtureはSynthetic Profileを使い、それらに加えてundirected edge key `(min(i0,i1), max(i0,i1))`をcanonical Triangle順で構築し、出現1回をBoundary、3回以上をNon-Manifold、2回でもdirected向きが逆でないものをOrientation不整合として数え、すべて0を要求する。binary32 weld後のTriangle edge adjacencyから連結成分を再構築し、成分はその成分が含む最小canonical Triangle indexの昇順、成分内Triangleはglobal canonical Triangle順を保つ。

Synthetic Watertight Fixtureのsigned volumeは成分ごとに次の`SolidSignedVolumeV1`だけで計算する。成分で参照されるpositionをcanonical position index順に走査してbinary64のcomponent Bounds `min`／`max`を求め、参照点を各軸について`r = min + (max - min) * 0.5`の順で計算する。Triangle `(v0,v1,v2)`ごとに`a=v0-r`、`b=v1-r`、`c=v2-r`、`q=crossRH(b,c)`、`numerator=-dot(a,q)`、`term=numerator/6.0`をこの順にbinary64で評価する。`V0=+0.0`から成分内canonical Triangle順に`Vk+1=Vk+termk`を左畳みし、除算後のtermだけを加算する。式の再結合、原点基準への置換、pairwise／Kahan加算、FMA、除算の後回しは禁止する。成分Boundsから共通Numeric Kernelで算出した`epsVolume`に対し、有限な`V > epsVolume`だけを合格とし、`V == epsVolume`を含む`V <= epsVolume`、負値、非有限値をRejectする。Synthetic Validation Result用の全体Volumeは成分順に各合格`V`を同じbinary64左畳みで加算し、途中または最終値が非有限ならRejectする。

`SolidGeometryValidatorV1`はSynthetic Watertight ZCG bytesを入力とするversion固定の共有Validatorを唯一の正本とし、Synthetic Profileの`SelfIntersectionAlgorithm`は`ClosedTriangleDistanceV1`だけを許可する。Synthetic Fixture Generator／Blender HarnessはPython独自predicateを実装せず、ZCG encode後にSynthetic Script Bundleへhash固定された共有Validatorを呼び出す。Unity Editor側のSynthetic Dataset検証とT-081も同じValidator artifactを使用する。実装artifact、CLI引数、終了codeはSynthetic Script Bundle hashの対象とし、利用不能・version不一致・未知algorithmをSynthetic Validation失敗として扱い、別ライブラリへFallbackしない。Licensed Harness、Licensed Report、製品Preprocessorからは呼び出さず、生成前Gateの結果や元Blender doubleで再判定しない。

`ClosedTriangleDistanceV1`はbinary32から正確にbinary64へ展開した2つの閉Triangle間の最小二乗距離を決定論的に求める。候補は、Aの3頂点から閉Triangle Bへのpoint-triangle二乗距離、Bの3頂点から閉Triangle Aへの同距離、Aの3 closed edgeから閉Triangle Bへのsegment-triangle二乗距離、Bの3 closed edgeから閉Triangle Aへの同距離、AとBの各3 edgeによる9組のclosed-segment間二乗距離の順とし、各群内はlocal vertex／edge番号の辞書順で評価する。segment-triangleはsegmentとTriangle planeの交点parameterが閉区間`[0,1]`にある場合、固定式で`u`、`v`、`w=1-u-v`の順にbinary64 barycentricを計算し、`u >= 0 && v >= 0 && w >= 0`ならface interior／boundary貫通として距離0のwitnessを返す。等号は包含し、比較不能／非有限ならこの0距離分岐を採用せず後続の保守的距離候補へ進む。非平行時のplane交点、平行／coplanar時の3 edgeとのsegment-segment、両endpointのpoint-triangle候補を固定順に評価するため、「一方のedgeが他方のface内部を貫通するが頂点もedge同士も接触しない」proper crossingも検出する。

point-triangle、segment-triangle、segment-segmentはversion固定のEricson型region testを、`ZcgNumericKernelV1`のbinary64演算順、`dot`、`crossRH`、除算、clampへ逐語的に固定した共有実装とする。各候補は二乗距離だけでなく両Triangle上のclosest witness `(pA,pB)`と各Triangleのbarycentricを返す。barycentric値およびsegment parameterの`0`と`1`は閉区間へ含め、clampは`x < 0 ? 0 : (x > 1 ? 1 : x)`、候補minimumはstrict `<`の場合だけ更新して同値なら先の候補を保持する。退化Triangleは先行Triangle Gateで、zero-length edgeまたは非有限な分母はTopology／退化Rejectで到達不能とし、predicate内で別形状へ降格しない。`epsDistanceSquared=epsDistance*epsDistance`もbinary64でこの順に一度だけ計算する。

自己交差候補は全`TriangleCount choose 2`を走査せず、version固定の`SolidCandidateBvhV1`で生成する。各Triangleのbinary64 AABBを各軸の正負へ`epsDistance`だけ拡張し、非有限化またはdomain Boundsを越える算術overflowをRejectする。primitive初期順はcanonical Triangle index順とし、各nodeでTriangle centroid Boundsのextentが最大の軸をsplit axisに選ぶ。同値はX、Y、Z順、軸上のstable sort keyは`centroid[axis]`のbinary64 total-order、次にcanonical Triangle indexとする。個数`n`のnodeは`floor(n/2)`で左右へ分割し、leafは1 Triangle、node IDはpreorderで付与する。比較、Bounds union、中央値、node作成順をこの規則から変更せず、SAHや並列schedule順をcanonical結果へ使わない。

候補生成はroot対rootから始める。同一node pairでは`(left,left)`、`(left,right)`、`(right,right)`、異なるnode pairではAABBが全3軸で閉区間交差する場合だけ下降する。両方leafなら`a < b`へ正規化してpairを出力し、片方だけ内部nodeならその左右を順に、両方内部nodeならprimitive数の多い側を分割し、同数ならnode IDの大きい側を分割する。この規則により各unordered leaf pairを最大1回だけ生成するが、出力後もuint32 `(a,b)`のradix sortで昇順へ正規化し、隣接重複を除去してから狭域判定へ渡す。重複の有無を診断値へ残し、重複があってもdeduplicate後の意味は変えない。

候補counter、node数、byte数はchecked unsigned 64-bitで配列確保前とappend前に検査する。一意候補がSynthetic Profileの`MaxCandidatePairCount=2000000`へ達した後、次の異なるpairを検出した時点で追加割当や狭域判定を行わず、Synthetic Validation Resultの`SelfIntersectionCandidatePairCount`を`MaxCandidatePairCount + 1`、結果を`CandidatePairLimit`不合格として終了する。候補counter、`2 * TriangleCount - 1`のnode数、pair／node byte長のいずれかがchecked overflowする場合も、割当前に同じsentinelと不合格へ収束させる。Triangle AABBのepsilon拡張だけが非有限化した場合は`NonFinite`不合格とする。これらをLicensed ProfileUnsupported／Resource retryへ変換せず、Synthetic Harness固有の固定容量失敗として扱う。

sort／deduplicate後の候補だけをcanonical pair `(a,b)`昇順に処理し、AABB broad phaseを通らなかったpairへnarrow phaseを実行しない。共有position index数が0なら`ClosedTriangleDistanceV1`、1または2なら下記`SharedSimplexIntersectionV1`で分類する。共有indexだけを理由に候補pairを除外しない。

共有indexが1または2のpairには、同じ共有Validator artifactに含まれる`SharedSimplexIntersectionV1`を適用する。共有1 indexならそのpositionを閉point、共有2 indexなら2 positionをcanonical index昇順で結ぶ閉segmentとして共有simplex `S`を定義する。閉Triangleの実交差集合`A intersection B`が`S`内だけなら許可し、`S`外にも接触・交差があれば自己交差1件とする。共有simplex外のcoplanar overlapとproper crossingを含む一方、正常な隣接面間のepsilon近接だけでは自己交差としない。許可領域をepsilon近傍へ拡張せず、epsilon近接集合の包含証明や残余距離の制約最適化は行わない。

`SharedSimplexIntersectionV1`は既存のbinary32復号値／binary64 Numeric Kernelを用い、coplanar時はdominant-axisへ射影した2D閉Triangle clipping、非coplanar時は各方向3 edgeのsegment-triangle交差から実交差の共有simplex外への広がりを検査する。共有部で距離0になる最小距離witnessだけで判定しない。coplanar判定、clippingのinside判定、segment-triangle判定および共有simplex内外の判定は、固定binary64演算の符号・等号・閉区間比較だけで行い、narrow phaseに`epsDistance`その他のepsilon包含を適用しない。ここでいう実交差はこの固定predicateが返す交差を意味し、実数幾何の完全証明を要求しない。先行Gateを通過した有限・非退化入力は、中間演算も有限なら必ず許可／自己交差の二値へ分類し、分類不能という第三状態を持たない。非有限演算は既存`FailureReason=NonFinite`へ送る。別ライブラリやepsilon近接判定へのFallbackを追加しない。実装sourceとGoldenはSynthetic Script Bundle hashへ含め、更新前Validatorの結果を更新後の検証証拠として流用しない。Profile／Validation Resultのschema、Licensed処理は変更しない。

pair分類は次の完全決定表に固定する。

| 共有position index数 | 条件 | 判定 |
| --- | --- | --- |
| 3 | 任意 | 重複Triangleとして先行GateでReject。自己交差数へ到達しない |
| 2 | 実交差が共有edge上だけ | 正規の共有edge接触として許可。directed向き不整合は先行Orientation GateでReject |
| 2 | 共有edge外にも実交差あり | coplanar overlap等として自己交差1件。epsilon近接だけでは数えない |
| 1 | 実交差が共有vertex上だけ | 正規の共有vertex接触として許可 |
| 1 | 共有vertex外にも実交差あり | proper crossing等として自己交差1件。epsilon近接だけでは数えない |
| 0 | `minimumSquaredDistance <= epsDistanceSquared` | coplanar overlap、proper crossing、非共有vertex／edge／face接触、epsilon以内のnear missを区別せず自己交差1件として数える |
| 0 | `minimumSquaredDistance > epsDistanceSquared` | 非交差。自己交差数へ加えない |

共有indexなしのpairではTriangle間距離がepsilonちょうどなら自己交差、binary64でその直外なら非交差という既存規則を維持する。共有indexが1または2のpairにはこの近接閾値を適用せず、共有simplex外の実交差だけを数える。候補pairをcanonical順に分類して得た自己交差件数がSynthetic Profileの`MaxSelfIntersectionCount=0`以下であることを要求する。BVH、候補pair順、`MaxCandidatePairCount`とValidation Result schemaは維持する。

T-081の小さいSynthetic回帰Fixtureで、正常な閉Tetrahedronと軸平行boxの自己交差0、共有edge外のcoplanar overlap拒否、共有vertex外のproper crossing拒否、非共有pairのepsilon near-miss拒否（既存の等号／1 ULP境界を含む）を確認する。Licensed経路から本Validatorを呼ばないことも確認する。これらはpredicate／Golden試験とし、Dataset case構成や全三角化の網羅matrixを追加しない。

Synthetic ZCG後GateでBoundary、Non-Manifold、向き、自己交差、成分volume、Bounds、Triangle退化のいずれかが失敗したFixtureは`SyntheticFixtureValidationResult.Passed=false`とし、Synthetic Dataset Indexへ含めない。Validation ResultのTriangle数、連結成分、Bounds、Volume、Boundary／Non-Manifold／SelfIntersection Candidate Pair／SelfIntersection統計は合格・不合格ともZCG decode後の値を正本とし、canonical化前の値を残さない。Candidate Pair上限超過だけは完全列挙せず、規定のsentinel `MaxCandidatePairCount + 1`を保存する。この結果をLicensed Report／Receiptへ書き戻さない。

ZCG v1のbyte layoutと既存Golden bytesは変更しない。通常のschema byte上限は64 MiBを維持するが、Phase 0.2 LicensedのD-155で許可したSource／保存基底TriangleMeshの読込み・canonical encode・保存・再読込みの全境界で128 MiB（134217728 bytes）を許可する。Decoderは各上限以下の呼び出し側`maxBytes`を必須とする。HeaderとIndexのGeometryByteLengthを配列確保前に照合し、Licensed TriangleMeshは事前検証したArtifact／Recipe／Profileの対応により、許可Source／保存基底なら`MaxRetainedBaseTriangleCount`、その他は`MaxVariantTriangleCount`、Synthetic TriangleMeshはSynthetic Profileの`MaxTriangleCount`以下、PositionCountは対応Triangle上限の3倍以下、Phase 0.2 ConvexSetはHullCount == 1、4 <= V <= 255、FaceCount <= 2V - 4、全Face index数 <= 6V - 12へ制限する。PhysicsCookInput Bindingの128頂点制限はdecode成功後に別途検査する。保存基底はTriangleCount <= 2000000、PositionCount <= 6000000とし、通常Variantはそれぞれ200000／600000以下とする。PositionとTriangle indexの12 byteずつの配列にHeader／countを加えた長さをcheckedで検証する。JSON record／Dataset Indexのbyte上限は引き上げない。保存基底の用途Bindingは別途200000 Triangle上限を検査する。Dataset種別、許可RecipeとProfile hashは呼出側がDecoder起動前に固定し、ZCG内容から別Profileを推測しない。全record長はchecked 64-bit算術でpayload長と突き合わせ、overflow、宣言数過剰、途中EOFをRejectしてからだけ配列を確保する。未知Format／Versionを別形式として推測decodeせずRejectする。

`GeometryRelativePath`はGeometry Dataset rootからの相対pathで、CanonicalBundleIndexと同じNFC、`/` separator、segment、control文字、case-fold衝突、通常file限定の規則を適用し、小文字`.zcg`へ固定する。distinct Geometry Artifactごとに一意pathを持ち、NoOp／Alias／複数Bindingは同じArtifactを参照する。StructuralSlabFixtureはIndexで明示した`.json` pathに保存し、Report／Index／ReceiptはDataset payload rootの外へ置く。拡張子からRoleやprovenanceを推測しない。

Index Codecは10.2.2の成功出力tag、正確なArtifact／sidecar、用途Binding、親参照closure、Entry単位provenanceをReportへ照合する。NoOp／Aliasが参照する既存Artifactを重複生成・重複加算しない。VerifierはDataset rootを再帰列挙し、symlink／junction／reparse pointを拒否し、通常file path集合をIndexのGeometry／sidecar path許可リストと完全一致させる。欠落、余分file、重複／case-fold衝突、length／SHA-256不一致を拒否する。失敗予定EntryはReportだけに残し、Unselected親の支持用収録を測定対象への採用と混同しない。

`DatasetContentSha256`はcanonical `LicensedRepresentativeDatasetIndex` bytesそのもののSHA-256とし、後続`GeometryBenchmarkRunManifest`へ同じ`DatasetId`とともに格納する。変動するAttempt時間、Peak Working Set、HostProfileId、Report hashはDataset Indexへ含めないため、同じGeometry集合とTool／Profile hashなら実行時間が変わってもDataset hashは変化しない。

最終的な双方向監査は10.2.2の`LicensedFixtureSelectionReceipt` v2で閉じる。Report／Index bytesとReceiptのhash、SelectionRunId／DatasetId、Catalog、完了frontier／Batch Manifest列、採用実数を双方向照合し、`DatasetIndexContentSha256 == DatasetContentSha256`を要求する。Receiptは全検証後に最後に原子的確定するcommit markerとし、欠落／不一致／非終端batchをBenchmarkへ渡さない。Dataset hashを時間情報から独立させたまま、全失敗とAttemptを含む特定Reportへ結合する。

canonical Loaderのschema上限はProfile 64 KiB、Source Catalog／各Bundle Index 16 MiB・100000 Entry、Report 64 MiB・100000 PlannedVariantEntry・合計200000 Attempt、Dataset Index 64 MiB（Geometry／sidecar／Bindingの各列100000以下）、Receipt 16 MiB・100000 Batch参照以下とする。単体Artifact record／sidecarは16 MiB・100000要素以下、EarlyFixtureEligibilityRuleSetは64 KiB・厳密5 Rule、EarlyFixtureBatchManifestは64 KiB・最大12 Sourceを上限とする。各Loaderは正の`maxBytes`と必要な`maxEntries／maxAttempts`を呼出側から必須で受け取り、schema上限を超える呼出しを拒否する。全列の合計割当byte数もcheckedで検査し、無制限overloadを設けない。

Loaderは、(1) seek可能入力なら配列確保前に総byte長をschema上限と呼び出し側上限の小さい方へ照合する。非seek入力では有効limitを`min(schemaMaxBytes, maxBytes)`とし、最大`limit + 1` byteまで試読して、Parser bufferへ保持するのは先頭limit byteまでとする。`limit + 1`番目を1 byteでも取得した時点でSizeLimitExceededとしてRejectし、そのbyteをJSON parserやhashへ渡さない。ちょうどlimit byteでEOFなら受理可能とする。(2) JSON nesting最大8、単一string token最大1024 UTF-8 byte、property数を各固定schemaへ制限、(3) SchemaVersionと固定root property順を検証、(4) 宣言Entry／Variant／Attempt件数をschema上限と呼び出し側上限へ照合、(5) その後だけ配列を確保、(6) 全要素、実配列長、ordinal順、末尾dataなしを検証、の順で処理する。Reportの`AttemptCount`合計も`min(200000, maxAttempts)`以下かつ実Attempts総数と一致させる。Receipt Loaderは参照先を自動で無制限読込せず、検証側が各参照文書用の個別上限を明示して読み込む。

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

製品用点Anchorの初期所属Cellとlocal位置はRecipe Hashへ含め、別のAnchor Cache、Runtime再探索結果または頂点配列indexをCache Keyへ追加しない。Phase 0.2成果物はこの入力を持たず、同PhaseのCacheとSchemaを遡及変更しない。

### 10.8 公開リポジトリとライセンス境界

変換コード、汎用Recipe Schema、ライセンスAssetを含まないテンプレート、検証コード、Blender版Manifest、Bootstrapは公開する。Blender本体、Synty／Poly Pro Universeの入力Asset、`.unitypackage`、付属`.meta`、生成された共用Cut Geometry、Physics Proxy、加工済み断面素材は公開しない。`/Tools/Blender/`と`/Generated/`をgitignoreし、公開履歴への混入をCIで検査する。

Synty POLYGON City Packの購入原本は、公開Unityリポジトリと分離した非公開Git LFSリポジトリ`C:\Users\%USERNAME%\src\zantetsuken-assets-private`で管理する。2026-08-26時点で、`Vendor\Synty\POLYGON_City\v5\Original`へ`POLYGON_City_SourceFiles_v5.zip`と`POLYGON_City_Unity_2022_3_v1_12_4.unitypackage`を格納済みであり、両ファイルはLFS対象である。ダウンロード元と格納先のSHA-256一致を確認済みとする。

非公開リポジトリへのアクセスは各Assetライセンス上の許可を持つ開発チームだけに限定する。購入原本は変更せず保存し、展開したFBX／Texture、Phase 0.2のEarly Licensed Fixture／Asset対応表、加工済み共用Cut Geometry、Physics Proxyなどのライセンス派生物も公開Git履歴へ入れない。公開リポジトリから参照する場合も、公開Submodule、公開Release、公開CI Artifact、共有Cacheを経由してAsset本体を配布しない。

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
| D-003 | 即時応答 | 必要なベイク・VP変換後にGPU仮表示を先行し、共用VP Geometry／Convexを非同期更新（4.5） | 確定 |
| D-004 | 仮断面 | clipで分離、Stencilで仮断面、最終的に実断面へ置換 | 確定 |
| D-005 | 非同期整合 | ジョブ結果を世代番号で無効化・コミット制御 | 確定 |
| D-006 | 人形 | 切断時に姿勢固定し、静的Mesh／剛体破片へ移行 | 確定 |
| D-007 | アセット | Synty POLYGON City Packを主素材に採用 | 確定 |
| D-008 | アート | 共通セルシェーダ、輪郭線、限定パレット、独自看板で統一 | 確定 |
| D-009 | モーション | 既製Humanoidモーションをリターゲットし、IKで補正 | 廃止：Mob Prediction対象とそれ以外のIK scopeをD-136で分離 |
| D-011 | 対象環境 | 初期製品スコープをPCVRとし、Quest単体対応は当面除外 | 確定 |
| D-012 | 性能目標 | 実アプリの両眼描画90fpsを基準とし、再投影を常用前提にしない | 確定 |
| D-013 | 開発順序 | 非VR PoCと性能評価を先行し、早期XR確認後にVR操作・UIを導入 | 確定 |
| D-014 | 検証HMD | Quest 3Sを有線Quest Linkで初期PCVR検証に使用 | 確定 |
| D-015 | 攻撃演出 | 三日月形の斬撃波を扇状に有限速度で飛翔させ、接触時に分離 | 確定 |
| D-016 | 先行計算 | 到達猶予で未来姿勢、表示／Stencil共用VP Geometry、Convex切断を投機評価 | 確定 |
| D-017 | 未来評価 | 未来イベントDAG、世代検証、Commitから成る評価器を実装する。初期Dispatcherは固定PriorityClass、Deadline、stable順、固定容量、Schedule前取消だけのV1とし、費用学習・aging等は実測後に必要なものだけV2へ追加する | 段階導入で確定 |
| D-018 | 物理予測 | 適用条件を満たす自由飛行剛体はO(1)固定刻み直接予測を使用する。Gate対象外はPhase 4.5では後追い処理とし、Phase 4.6以降は接触・転動する対象のうち19.4で適用可能なものだけを局所PhysicsSceneで先読みする。成果物は実命中時に検証する | 確定。本体統合はT-017で検証 |
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
| D-067 | cooking非同期化 | Bake／cookingは即切断表示・初回仮運動のクリティカルパスから外す。表示は4.5.2の準備後に開始し、実Geometryは4.5.6の現在の参照・frameと転送条件で公開する。固定側も描画するが動かさない | 確定 |
| D-068 | Pending物理共有 | `PendingPhysicsSplit`中は左右の表示破片を1つのFragmentGroup、Rigidbody、旧Colliderへ追従させ、小幅のめり込みと隙間内の旧Colliderを一時許容する | 廃止（D-132で保守Fallbackへ限定） |
| D-069 | 物理分裂Commit | Bake済みConvexの完成後、物理ステップ境界で左右Rigidbodyへ分裂し、親の線速度・角速度から各重心位置の速度を継承する | 廃止（D-132でProvisional生成時の分裂とFinal handoffへ置換） |
| D-070 | Cooking Profile | Cleaning／Welding無効化を有力候補とし、Fast Cook／Fast Simulationの費用と効果を同条件で実測する | 技術検証付き確定 |
| D-071 | 二段階Collider | 初回はFast Cookで物理分裂し、価値のある破片だけを余剰時間に別MeshのFast Simulation Bakeへ昇格させる | 技術検証付き確定 |
| D-074 | 全体低重力 | 空中斬り猶予を増やすため世界全体を低重力にし、PoC仮値を約0.5Gとする。周辺物理値は先に作り込まずプレイ後に判断する | 技術検証付き確定 |
| D-075 | 重力一元管理 | `WorldPhysicsProfile`を正本とし、Unity Physics、未来予測、解析軌道、VFXへ同じ重力を供給してRunごとに記録する | 確定 |
| D-076 | 即時Shadow | 即時切断中は同じper-instance clip／分離Offsetを適用した両面ShadowCasterで影を近似し、Shadow Map用Stencil断面は描かない | 技術検証付き確定 |
| D-077 | Shadow Batch | Shadow描画をStable片面群とPending両面群へ分け、切断平面は固定長Instance Recordで渡して平面値・切断数によるDraw分割を避ける | 技術検証付き確定 |
| D-078 | 有限仮キャップ | 即時キャップ板をローカルOBBと切断平面の3～6頂点交差多角形から生成し、他のTemporary Render Boundary半空間でclipしてからStencilで実輪郭へ制限する | 確定 |
| D-079 | Stencil Color割当て | 左右眼いずれかで保守的な可視Cap Boundsが重なる非互換対象を通常Colorでは分離する。Conflict Graphは論理モデルに限り、全Graph構築、全組合せ走査、Greedy Coloring、stableなColor番号を要求しない。`MaxStencilColors`へ収まらない対象は最後のColorへ統合する | 技術検証付き確定 |
| D-080 | Stencil互換Group | 全World Cut Plane、Side／半空間、Offset、Cap描画状態が一致し、6章の共通入力Gateに合格したGeometryは向きと符号を保存したまま同じStencil Colorへ加算できる。Maskの意味は`sum(W_i) > 0`であり、正逆相殺による欠落を許容して幾何学的Unionを保証しない | 技術検証付き確定 |
| D-081 | 両眼Cap可視性Cull | 論理破片×切断面ごとに左右眼Facingを判定し、全Capが両眼とも裏向きの互換Groupは彩色前にStencil Clear／Volume／Cap処理から除外する | 技術検証付き確定 |
| D-082 | Stencil競合領域 | 集計後に`S != 128`となるResidual Stencil Supportを可視Cap Boundsで保守的に包み、Raw Stencil書込みの途中重なりは競合としない。各眼でOBB投影または可視Cap Boundsのどちらかが非交差なら通常Colorの共有を許可する。実StencilからSupportを検出・監視しない | 技術検証付き確定 |
| D-083 | バックグラウンド実行基盤 | CPU幾何・予測計算はC# Taskの大量発行ではなくJob System＋Burstを基本とし、Task／AwaitableはI/Oと非同期制御へ限定する。Unity Objectの適用とGeneration Commitはメインスレッドで行う | 確定 |
| D-084 | Convex Job Pipeline | Physics ProxyのConvex分割、検証、質量特性、MeshData出力と`Physics.BakeMesh`をJob化し、Mesh公開とCollider／Rigidbody Commitだけをメインスレッド／物理ステップ境界に残す | 技術検証付き確定 |
| D-085 | Native Cook比較Probe | Unity Built-in 3D Physicsの`Physics.BakeMesh`を製品経路の正本とし、Native PhysXの頂点Hull経路、完全Topology経路、直接生成経路を早期に測定専用Probeで比較する | 確定 |
| D-086 | Native採用Gate | Cook時間の倍率差だけでは置換せず、Unity経路が実際のP99／90Hz要件を破り、Unity側最適化で解消せず、Native統合Prototypeまで成立した場合だけ物理経路の部分置換を再検討する | 確定 |
| D-087 | Voxel後Surface Projection | 製品前処理としての採用を取り消し、制約付きSurface ProjectionはT-071の`Future Research: Global Solid Reconstruction`へ隔離する。Phase 5.5以前の依存、完了条件、製品Fallbackにしない | 廃止／研究隔離 |
| D-088 | 閉Topologyの自己交差契約 | Topological Watertightと自己交差のないGeometrically Valid Solidを区別する。自己交差はD-117の共用Geometryで許容する。個々のPhysics Convexには自己交差を許可しないが、Compoundを構成する別Convex同士のIntersection／Overlapは許容する。Geometrically Valid Solidは合成試験と将来研究だけの用語とする | 確定 |
| D-090 | ライセンスAsset保管 | Synty購入原本と派生物は、公開Unity Repoの兄弟に置く非公開Git LFS Repo`C:\Users\%USERNAME%\src\zantetsuken-assets-private`で管理し、許可されたチーム以外へ共有しない | 確定 |
| D-091 | 固定物体の切断 | 点FixedSupportAnchorの初期CellとfiniteなFrame内local位置を入力とし、現在Cellの集合だけを採用面とanchorEpsilonで正負／OnPlane両側へ継承する。所有者全体はAnchorがあれば固定、なければ動的とする。Shape共有・内接削減・再cook・反対側Geometry付属からAnchorを生成・移送しない | 確定 |
| D-092 | ゼロ幅切断 | Kerfは0、固定側のOffset／Impulseは0とする。両側固定でも通常の仮描画・実切断・Cap生成を行う | 確定 |
| D-093 | 切断痕の許容 | 固定側のCapと実Geometry境界の細い亀裂、輪郭線、線状Z-fighting、軽微なチラツキを許容する。通常Capは片面描画とし、正常な正向き閉Shellの面状Z-fighting・Cap欠落・混入は5.2の明示的例外以外では不具合とする | 確定 |
| D-094 | 状態の粒度 | 物理分裂、Geometry完成度、Work Result採否を独立管理する。所有者の固定／動的と表示の可否を分離する | 確定 |
| D-095 | Anchorと公開の実装順 | Phase 1で正負子・切断履歴の公開と単純Anchor配分を合成入力で確認し、Phase 4で実Convex・Actor・cookへ接続する | 確定 |
| D-098 | Pending Cutと描画集合 | 受付時はPending Cutだけ、正負の非空子・実在面参照の確定後にLogicalCutOperationを原子的に公開する。未公開親への後続受付は見送り、公開後はGPU／物理完了待ちへ制限を延長しない。仮表示Recordを同じ採用面で引き継ぎ、両用途Geometry Commit後だけ回収する。履歴と未完了物理工程は保持し、Operation未公開の終端失敗は4.2の限定退役へ従う | 確定 |
| D-106 | Geometry／Cook性能Baseline | T-070を早期Cook比較Probe、T-076を製品Geometry完成後の補完・再解釈Baselineとする。CPU側の共用VP Geometry切断、Convex切断、検証済みTemporary Physics Proxy、cookを工程別に、計算KernelのSingle-Thread µs/op、cook／Commitの単発Latency、Job Batchの定常Throughput／End-to-End latencyへ分けて測り、保守的なP95／P99容量式を作る | 確定 |
| D-107 | Benchmark Manifest分離 | GeometryBenchmarkRunManifestは単一Target／Stage／ExecutionMode／CookingProfile／Metric／Unitの1測定系列ごとに作り、BenchmarkSuiteIdで一回のHarness実行を束ねる。型・値域・組合せ・全property順・非該当値のJSON nullを固定し、canonical保存はclean Repositoryだけに許可する。既存TraceRunManifestとCodec／Golden Hash／bundle形式を変更しない | 確定 |
| D-108 | Benchmark Result Bundle | 各Run Manifestへraw Samplesと固定Aggregateを持つGeometryBenchmarkResultを1対1対応させ、GeometryBenchmarkSuiteIndexがManifest／Result hashと件数を固定する。Suite開始・終了時に同じclean HEADを検証し、Repository外でIndexを最後に書いて原子的に確定する | 確定 |
| D-109 | Benchmark Case／Loader契約 | 1 Manifestを単一DatasetCaseIdと固定規模軸へ限定し、Manifestの説明変数とResultの測定値から容量式を復元する。Target×Stage×ExecutionModeを許可表で検証し、Result v1は100万Sample／64 MiBをschema上限、呼び出し側の明示上限を必須とする。Bytes／Countのraw値と順序統計量は整数に限定するがMeanはcanonical doubleとする | 確定 |
| D-110 | Benchmark集計／全Loader境界 | Percentの0..100制約からAggregate.Countを除外し、Meanを取得順binary64左畳みで固定する。Rejectedは対象結果を観測不能な試行だけとし、対象処理の失敗はFailureRateへ含める。Manifestは64 KiB、Indexは10万Entry／64 MiBを上限とし、Manifest／Result／Indexの全Loaderへ呼び出し側上限を必須とする | 確定 |
| D-111 | Suite内Dataset同一性 | 同一BenchmarkSuiteIdでは1つのDatasetIdを厳密に1つのDatasetContentSha256へ対応させ、Suite Loaderがjoin前に全Manifestを検証する。異なるDataset版は別Suiteまたは明示的な別DatasetIdとして測定する | 確定 |
| D-112 | 早期実Asset Fixture | Phase 0.2でSynty／Poly Pro Universe等の多数モデルへ共通の簡易Blender処理を適用し、Render／Convex Gateを自動通過した少数だけを非公開LicensedRepresentative Datasetへ固定する。Watertight既知正解は別のSynthetic Fixtureから得る。個別修理と最終最適化は行わず、投入母数とReject理由を保持し、全Asset互換性の証拠にはしない | 廃止。履歴として保持し、Phase 0.2の現行正本はD-148へ置換 |
| D-113 | 早期Triangle Variant | Phase 0.2のLicensed Render FixtureはOriginalと約100／500／1,000／2,000／5,000／10,000 Triangleを要求する共通Decimate Presetで生成する。TargetはPreset名であり正確な出力数を要求せず、Target／SourceからRatioを1回算出して反復探索せず、canonical化後ActualをBenchmark規模軸とする。Source／Voxel基底がTargetを上回れば削減率に関係なく生成し、Target以下のNoOpと同一hash Aliasだけを重複Geometryから除外する。Synthetic Watertight Fixtureの規模系列とConvex削減系列は別にする | 廃止。履歴として保持し、Phase 0.2の現行正本はD-148へ置換 |
| D-114 | 早期Voxel Variant | Voxel64／128／256をTopology再構成系列としてDirect Decimateと分離し、SourceとのTriangle差や増減にかかわらず基底Variantを保持する。限定Post-Decimate行列だけを生成し、各結果を再検証して大偏差はBenchmarkOnlyとする | 廃止。履歴として保持し、Phase 0.2の現行正本はD-148へ置換 |
| D-115 | 早期Fixture canonical契約 | 数値Gate、カテゴリ、Triangle帯、決定論的／資源上限をEarlyFixtureSelectionProfileへ固定し、Import前のSource母集合とPhase 0.2 EligibilityをEarlyFixtureSourceCatalogへ固定する。Source／Script／Presetはcanonical file index bytesでhashし、Blender実行前とReceipt確定前に実treeとの完全一致を再検証する。VariantIdはSource＋Tier内で一意、DatasetCaseIdはTierを含める。Selection ReportはEligible Sourceだけを対象にLaunch／Bootstrap／Importを区別した完全決定表に従うStatus／Attempt列と変動時間を記録する。Licensed採用GeometryはZantetsuCanonicalGeometry v1へ正規化し、binary32 decode後にRender／Convex Gateを再実行する。Synthetic Watertight Geometryは別DatasetとGenerator hashで固定する。LicensedRepresentativeDatasetIndexは再検証合格GeometryのFormat／Version／相対path／byte長／canonical file hashを完全なfile許可リストとしてTool／Profile hashとともに確定する。Index canonical bytesのSHA-256をBenchmark DatasetContentSha256とし、Report／Index両hashをLicensedFixtureSelectionReceiptで監査可能に固定する | 廃止。履歴として保持し、Phase 0.2の現行正本はD-148へ置換 |
| D-116 | Synthetic自己交差Broad Phase | Synthetic Watertight Fixtureだけについて最大20万Triangleの全pair列挙を禁止し、epsilon拡張AABBの決定論的`SolidCandidateBvhV1`で候補を生成してcanonical pair順へsort／deduplicateする。200万一意候補をSynthetic Profile上限とし、次のpairでSynthetic Validationを`CandidatePairLimit`不合格へ停止する。Licensed Fixture、製品Preprocessor、Runtimeでは実行しない | 確定 |
| D-117 | 表示／Stencil共用Geometry | 切断対象は閉鎖・edge／vertex manifold・局所winding整合済みの一つの共用Geometryを表示とStencilへ使用し、一度の切断・Cap生成で同じ不変条件を各出力へ継承する。Self-intersection、別Topologyの閉Component間のIntersection／Overlap、Internal／Nested／Coincident、全体反転、Runtimeの面積0 Triangleは許容する。入力不合格や不正出力を用途別Geometry、修復、簡易表示Proxyで救済せず、全Meshのinside／outside、自己交差、向き正規化をRuntimeへ追加しない | 確定 |
| D-119 | 正符号8bit Stencil | 専用8bit Stencil Byteを128へ初期化し、IncrementWrap／DecrementWrapで`S=(128+W) mod 256`を得て`S>128`だけを描画する。Winding上界の証明・検査・容量分割は行わず、範囲外の誤描画は5.2の品質例外とする。8bitを排他利用できない構成ではゲームを開始せず、部分Bitや代替経路を作らない | 確定 |
| D-120 | Convex由来の質量特性 | D-133でFinal正本とProvisional近似を分離したため旧契約を廃止する | 廃止 |
| D-121 | 非Union標準Asset表現 | 共用Cut Geometryと幾何Topology、Compound Physics Proxy、必要な点Anchorを標準とする。別TopologyをUnionせず切断・Capし、通常結果を正負二集合へまとめる。Capは向きを保存してsum(W_i)>0で描く。製品用Strict Solidは生成・常駐・Fallbackせず、Global Solid Reconstructionは将来研究に限る | 確定 |
| D-122 | Phase 0.2簡易Boundary Fill | Poly Pro Universeで有効性を確認したBlenderのNon-Manifold選択＋`F`相当を早期Fixture候補へ加える。本命は次数2のBoundary Loopを個別封鎖する`BoundaryLoopFill`、人手操作に近い`BlindNonManifoldFill`は厳格な事後Gate付きBenchmarkOnly探索とする。元Geometryを上書きせず、別Object／Componentの結合やBoolean Unionを行わず、失敗VariantだけをRejectする | 技術検証付き確定 |
| D-123 | 大型建物の形状近似 | 外周Structural Slab 4枚以上と、Slabごとに原則1個・入口等では少数の直方体Convexを同じCompoundへ持たせる。装飾は共用Geometryへ含め、厳密な建築構造解析とBoolean Unionを要求しない。固定は点Anchorの有無で決める | 技術検証付き確定 |
| D-124 | Player非接触 | 初期仕様ではPlayer Body／Handとプロップ／破片のPhysX接触を無効化し、刀と斬撃波だけを論理SweepでInteractionさせる。移動可能域とCamera壁内侵入防止は簡易Occupancy Queryと視界保護へ分離する | 廃止（D-131で限定保証へ置換） |
| D-127 | 即時Clip Plane予算 | D3D11 PoCは`SV_ClipDistance` 8面を性能／MSAA品質上の優先経路、Pixel Shader `clip()` 4面を固定Fallbackとし、RenderFragmentごとの容量超過面は即時Color／Depth／Shadow／Stencil Volumeからだけ無視する。Operation公開前は受付済みPending Cut、公開後は当該Fragmentの未Commit面・Sideを候補とし、Pending Cut列とCutBoundary公開列を受付の古い順に辿る未Commit祖先優先のdependency-closed prefixを左右眼と全Passへ共有する。Operation公開時も同じ受付位置と面を維持し、重複登録しない。新しい後発境界の即時表示より祖先半空間とSibling分離を優先し、論理履歴、背景Geometry／Physics処理、Cap Record集合を変更しない。Ignored VolumeのCap板は残してよく、最後の統合Colorでの可視化と誤Depthを5.2の品質例外とする | T-089付き確定 |
| D-128 | Phase 0.2 Building Scope | Poly Pro UniverseのBuildingはSource Catalog全体を母集合として保持しつつ、人間が処理前に豆腐型と判定して固定した`EligibleBoxLikeBuilding`だけを自動選抜へ投入する。複雑形状の除外をGeometry失敗へ数えず、Catalog hashからEligibilityと理由を復元し、成功率をEligible集合内だけで報告する | 廃止。旧識別子を含む履歴として保持し、豆腐型Scope方針とv2表現はD-148へ継承 |
| D-130 | 最小優先度Dispatcher | 初期`FutureEvaluationDispatcherV1`は固定PriorityClass、Deadline、受付成功時に内部Recordへ発行するstable EnqueueSequence、固定容量、Critical予約枠、Schedule前取消、Dispatch／Completion予算だけを扱うMain Thread Soft Real-Time Dispatcherとする。入力`EvaluationWorkItem`はSequenceを持たず、Schedule済みJobを中断しない。物理安全、命中済み物理、命中済み表示、近締切投機、Backgroundの順を固定する。Dispatcher APIは非待機を維持するが、受付済み切断の必須仕事が実Queue満杯になった場合だけ、切断Coordinatorは既存Schedule／Completion／Result適用を同期進行して一度再投入できる。進捗不能または次のPhysics Stepが必要ならPendingへ持ち越し、PlayerLoop／Commit再入、全切断待機、無限再試行を禁止する。内部実装は破棄・交換可能とし、Producer／DAG／Kernel／Commitから見えるAPI、不透明WorkToken、Trace、Generation契約を維持する。費用学習、aging、work stealing、厳密予約は実測後に必要なものだけV2へ追加する | T-090付き段階導入 |
| D-131 | Player非接触の限定保証 | Player Body／Handとプロップ／破片のPhysX接触を無効化し、刀と斬撃波は論理SweepでInteractionさせる。人工移動によるモデル化済みOccupancyへの代表的な新規侵入だけを簡易Queryで抑え、実空間HMDはClampしない。視界保護は既存Occupancy／Boundsと安価なFade等によるbest-effortとし、過剰反応、見逃し、Camera被り、物体内部視点、Near Planeでの内部面、即時Stencil処理中の部分Cap／Cap欠落／余計なCap／Stencil混入／左右眼差を許容する。Camera overlapを切断、物理、Geometry Commit失敗へ昇格せず、完全なMesh検査、Stencil修復、Job再発行、同期Fallbackを行わない | T-088付き確定 |
| D-132 | Provisional Rigidbody／Collision Proxy | 点Anchor配分と所有者単位の固定／動的の導出後、Final Convex cookを待たず各物理子へRigidbodyを作り、既存cook済みConvexを再cookなしで再利用する。非交差Convexは該当側だけ、交差／曖昧Convexは両側へShape Instanceを割り当て、同系譜Sibling Collisionだけを無効化して外界Collisionを全て有効にする。Ghost Contactと早い接触を許容する一方、Provisional質量は既存のOBB切断近似、失敗時は等Weightで親Canonical Mass Budgetを保存する。Final handoffでは物理Actorのpose／COM線速度／角速度を正本として動かさず、包含検証済みFinal Shapeとmass／COM／inertiaを同一Actorへ置換する。表示は物理Actorへ追従し、frame差による瞬間的な表示移動を許容する。点Anchor配分の未完了は既存Work依存とし、受付済み処理の一時的なActor／Shape／Constraint実行枠不足では既存物理を維持したPendingとして必要な処理を継続する。要求構成自体が固定上限を超える、Backend共有不可、または既存の安全規則に従って資源確保・原子的構築を成立させられない場合だけ、利用可能な既存物理表現を正式採用して`Stable Unsplit`で終端できる。Provisionalを現時点で生成できないことだけで後続のFinal物理処理を打ち切らない。公開後の非finite、速度超過、Constraint破綻ではGroup全体を不可逆な`ProvisionalFaultFrozen`へ封じ込める | T-091付き技術検証確定 |
| D-133 | Final質量正本とProvisional近似 | Final質量特性はPhysics Convex B-repと`PhysicsConvexMassWeight`を正本とする。ConvexをLocal ID順binary64左畳みでfiniteかつ正和へ正規化して親質量を配分し、非交差Weightは継承、交差Weightだけを正負出力B-repの有効体積比で分ける。各出力の体積、重心、密度1慣性を求め、`assignedMass / convexVolume`でscaleして平行軸合成し、失敗時は規定のConvex OBB、Fragment OBB／AABB、現物理の正式採用の順へ低下する。cook待ちProvisionalだけは現在OBBの一段平面切断体積比、失敗時は等WeightでCanonical Mass Budgetを保存した近似mass／COM／inertiaを使用し、Finalへ昇格しない。handoffでは物理Actorのpose／速度とFinal Colliderの非張り出しを表示連続性、質量、運動量、角運動量、運動エネルギーの連続より優先する | T-085／T-091付き技術検証確定 |
| D-134 | Mob軌道Cacheの段階導入 | `MobPlan.RootTrajectory`の初期生成方式を、副作用のない固定ステップ二相更新、Waypoint／Lane Desired Motion、固定長未来Sample Queue、再生補間、移動距離由来`ExplicitAnimationStateV1`、`PlanGeneration`による粗い全Plan／Group無効化とする。Nearは同じKernelをライブ実行し、Mid／Farは有効なQueueを主に再生する。初期成立条件へORCA、依存Graph、部分再計算、Flow Field、軌道圧縮、時空間予約を含めず、実測後の後段最適化とする | T-092付き段階導入 |
| D-135 | 明示Animation State正本 | Current／Future AnimationのState、Clock、Transition、Blendの意味上の正本をゲーム側`ExplicitAnimationState`とGlobal FixedStepIdとする。Animator／AnimatorController／AnimatorControllerPlayableは任意のPose出力／Preview／Legacy Backendへ降格し、内部状態の読戻しやController逐次rolloutを標準MobPlan経路にしない。現在表示、未来切断、CPU Skinningは同じ対象Stepへ解決済みStateから交換可能なPose Evaluatorへ分岐する。ClipのLoop／Clamp、canonical duration、Source Time写像は`AnimationAssetSetVersion`へ結合したCatalogを正本とし、V1予測対象ではプロシージャルIKを無効化する。全骨Poseの全Mob／全Sample先行保存は行わない | T-018／T-044／T-046付き確定 |
| D-136 | IK／Pose Layer scope | Phase 4.7のV1予測対象NPCはCatalog登録済みのリターゲット済みClip Poseだけを使い、Look、腕IK、Foot IK、左右反転等を現在表示と未来評価の双方で無効化する。補正や反転はLayer入力Snapshot、weight／mode、適用順、Generation、Identityをimmutableな共通Pose Evaluation Inputへ追加して全Backendで同じ処理を行える後段だけに許可する。VR Controller実測姿勢から表示するプレイヤー腕のTwo Bone IK等、Mob Predictionへ入力されないIKは別scopeとしてこの制限の対象外とする | D-009を置換。T-018付き確定 |
| D-137 | Capture Profile分離 | Phase 0.11の短時間NVENC成立確認は`NvencBringUpProfileV1`（Windows／NVIDIA／D3D11、SDR／sRGB、左眼30fps、1280×720固定のRGBA8 sRGB入力、BT.709 limited-range NV12変換）を使い、Phase 4.8のOpenXR Projection Captureは`OpenXrProjectionCaptureProfileV1`（Windows PCVR／D3D11、SDR／sRGB、MSAAなし、Dynamic Resolutionなし、Single Pass Instanced、App Projection Layer 1枚、左眼45fps）を使う。30fpsと45fpsを同じ正本値として扱わず、Profile IDをCapture EnvelopeとRun Manifestへ記録し、Artifactは所属RunとFrame Relationから同Profileへ結び付ける。入力寸法、ImageRect、PixelLayout、GraphicsFormat、orientation、色変換の不一致またはRun中変更ではゲームを止めずCaptureだけをFail Fastする | D-064を置換。詳細は21.15。確定 |
| D-138 | NVENC bounded chunk bring-up | Phase 0.11のnominalは指定GPU／Driverを持つTier C hardware qualificationでだけ30fpsの120 cadence tick／4秒提出窓と提出後30秒のFinalization期限を使う。fault／Backpressure／Freeze／deadlineはTier Aで最大16 tick相当のfake clock／fake completionによる決定論的stepへ置換し、実時間1秒／10秒を待たない。一般CIは120 Frame、実NVENC、外部Decoder processまたは実process再起動を要求しない。実再起動Recoveryの期限はTier Cの代表caseだけで新processが`BeginRecovery`へ到達した時点から測り、process起動時間を別診断値とする。NVENC出力は複数Frameのraw Annex B Access Unitをaccepted順に連結するboundedなRun chunk Artifactとし、Phase 0.11は1 Run＝1 chunkを固定する。Frameごとのfile、hash、flush、rename、Artifact Completionを禁止し、全Accepted encode／appendのdrainとFrame Completion回収後、Workerの`TryJoin`前にchunkのhash確定、close、renameとContext terminal result生成を各1回行う。単一の`NvencCaptureRunCoordinator`がRun開始からPlan commitまたはAbortまで`NvencRunChunkContext`、Session Ownership Lease、Trace Freeze状態、Coordinator内部Registry slotおよびDispositionを所有し、別CoordinatorへのOwnership Bundle移譲を行わない。Phase 0.11では`Flush(true)`を要求しない。書込み中、Plan未登録またはcommit結果不明のchunkはprocess crash、device loss、強制終了等で全体を失ってよく、部分修復・部分公開を行わず次回Recoveryへ判断を委ねる。NVENCはPNG用RGBA CPU readbackを通さずGPU TextureからNV12変換して圧縮bytesだけをCPUへ回収する。Phase 0.11のchunk形式を製品用連続録画形式とせず、複数chunk、正式なchunk長、GOP／Container／segment、durability頻度、index／seek／保持期間はPhase 4.8で実測して決定する | D-137を補足。Phase 0.11／4.8境界として確定 |
| D-139 | Capture thread非待機とCompletion分離 | Phase 0.1は固定Unity版でthread-safe契約を持つ現行`ImageConversion.EncodeNativeArrayToPNG`をWorkerから直接使い、Main Thread PNG Fallbackを持たず、既存の固定容量Completion Queueを維持する。Phase 0.11はMain ThreadとRender Threadの双方でNVENC Completion、GPU Fence、bitstream取得、hash、file I/Oを待たず、Render Thread／Native Plugin callbackはboundedなGPU work登録だけで戻る。入力`CaptureSurfaceLease`はGPU変換がSource Textureを参照し終えた非同期証拠後、NVENC Input SlotはNVENC完了とbitstream所有権移転後にそれぞれ別々に解放する。NVENC完了回収、accepted順整列、chunk append、streaming hash／ByteLength更新、Frame Completion生成を単一専用Workerへ直列化し、各Frameのencode終端を短いlockを許容する固定容量SPSC Frame Completion Queueからexactly onceで通知して`ProducedArtifactCount=0`とする。Run chunkの確定はFrame Completionと分離し、CoordinatorがContextの`Finalized`／`Abandoned` terminal resultをbounded pollする。待機可能な完了処理は専用Workerが担当し、いずれのPool、Queue、Work Slotまたはchunk buffer枯渇時も待機せずBackpressureまたはCapture失敗終端とする | 一部廃止：Phase 0.1部分は継続し、Phase 0.11の単一物理Worker／reorder部分をD-142で置換 |
| D-140 | Captureテスト実行階層 | Phase 0.1／0.11のRuntime安全契約を変えず、試験をTier A通常CI／毎コミット、Tier B対応環境NVENC統合、Tier C hardware qualification、Tier D手動診断へ分離する。Tier Aはfake clock／GPU Fence／NVENC completion／Publication Serviceと数Frame・小payloadだけで状態機械と安全不変条件を検査し、実時間待機、実NVENC、FFmpeg、実process再起動、大容量fileまたは実障害を禁止する。Tier Bはsource-controlled trigger manifestが要求する依存範囲変更時に短い実native結合だけ、Tier Cは承認対象candidate自身のbuild identityへ結合した120 Frame nominalと代表Recoveryだけ、Tier Dは実hang／device loss／process kill／disk full／最大chunk／長時間Captureだけを扱う。分類不能な変更はTier Bへfail closedとし、Tier C通過済みbuild artifactだけをPhase承認／リリース相当へ昇格する。上位Tierは下位Tierのfault直積を再実行せず、実環境結合を証明する最小sentinelだけを重ねる | テスト累積時間削減、遅延検出および保守的な条件付き実行として確定。詳細は21.15 |
| D-141 | Capture Artifact streaming verification | 共通Artifact Storeのlength／SHA-256検証は、Artifact全長に比例する配列を確保せず、事前上限付き固定／bounded pooled bufferと同一open handleを使うO(1) memoryのstreaming verificationとする。Phase 0／0.1のstaging／final検証とdurabilityは維持する。Phase 0.11 Freshはtrusted internalな`NvencChunkFinalizationResult`、同一process／Run／OS lock／Context、close済み確定staging、非上書き同一filesystem移動を全て満たす場合だけstaging全hashを省略し、finalを1回streaming検証したPublish Receiptを同一PublicationのCaptureCompleteへ再利用する。Recoveryはprocess-local結果を信頼せず再検証する | Artifact全長配列、同一Freshでの多重全hash、Main／Render Thread検証を禁止。詳細は21.15 |
| D-142 | NVENC ordered two-worker pipeline | Phase 0.11は単一Session／単一論理Consumerの内部を固定2本のOrdered Submit WorkerとOrdered Output Workerへ分ける。Accepted線形化点で固定Submission Queue末尾へ入ったFIFO順を正本とし、Work TokenのSlotIndex／Generationを数値sortしない。Submit WorkerはFIFO先頭のGPU変換完了を待って同順に`NvEncEncodePicture`を呼び、各Accepted Workについて排他的な`Submitted`または`FailedBeforeSubmit` recordを容量8の固定SPSC Submit-to-Output Queueへ厳密に1件、同順で渡す。Output Workerは同Queue先頭だけを処理し、`Submitted`ではD-147のOutput Collectorが対応Event／Output Bufferからowned Access UnitへcopyしてNVENC Sampleを返し、同Worker上の同期Run Chunk Sinkがchunk append、hash／length、Frame Relation、owned領域返却を行う。`FailedBeforeSubmit`ではAccess Unit／Sinkを経由せず所有資源を安全に解放する。安全な所有権状態を確認できない場合は同variantを推測せずD-143のprocess-wide Poisonへ進む。Frame Completionは`Submitted`ではSink同期結果後、`FailedBeforeSubmit`では資源解放結果後にOutput Workerだけがexactly once生成する。後続Eventの先行signalを探索せず、任意順Completionのreorder状態を持たない。GPU変換、先行NVENC処理または同期Sink I/OによるHead-of-line blockingとCapture Backpressureを許容する。WDDM非同期NVENC、単一Session、固定2 Workerまたは順序不変条件を利用できない構成と順序違反では、reorder、複数Session、同期FallbackまたはBitstream解析を追加せずCaptureだけをUnsupported／Fail Fastとする | D-139のPhase 0.11部分を置換しD-147でOutput内部所有権を補足。Phase 0／0.1、Publication／Recoveryは変更しない。T-054と21.15で短いTier B／C確認を行う |
| D-143 | Phase 0.11 process-wide fail-stop | Production Composition Rootは非staticな`NvencCaptureProcessState`をprocessにつき厳密に1個生成し、Backend、Run Coordinator、Publication Serviceへ同じ参照を注入する。状態は`Running / Draining / PoisonedUntilProcessRestart`だけとし、native NVENC、GPU Completion、device lossまたはOS I/Oで安全な結果／所有権／Service静止を確認できない場合は一方向にPoisonする。Poison後は新規受付、Completion／Context terminalの推測生成、append、Finalize、Plan／Publication、OS lock解放、同process RecoveryおよびBackend再生成を禁止し、資源別Quarantine台帳や帰還後の再収束を行わない。実処理が後から帰還してもPoisonを解除せず、process再起動だけを復旧境界とする。Tier AはFixtureごとに独立したtest stateをfake構成へ注入し、ProductionにReset／Unpoison APIを設けない。実NVENCはUnity Editor process内で非対応とし、Tier B／Cと実Captureはstandalone Player／専用processで行う | D-138／D-142の制御喪失時契約を置換。正常経路、制御可能な失敗、Phase 0／0.1および次回processの既存Recoveryは維持する |
| D-144 | Phase 0.11 Encode Sample固定容量 | `NvencBringUpProfileV1`はNV12 Input Surface、対応するD3D11／NVENC登録・mapped所有状態、Output Bitstream Buffer、Completion EventおよびGPU変換完了証拠を論理的に束ねたCapture専用`NvencEncodeSampleSlot`を固定8組持つ。Work Slot 8件とは寿命の異なる別Poolとし、Accepted中だけ1件ずつ排他的に束縛する。Phase 0.11 ProducerのSource Surface Lease PoolとGPU変換同期資源も最大8 in-flightを支え、Work／Sample／Queue／Completionの実効上限を8へ揃える。Run開始前に8組と全固定資源を一括確保できなければ4組へ縮退せずCaptureだけをUnsupportedとし、Poison時は8組全てをprocess終了まで通常Poolへ戻さない。8組は短時間jitterの固定余裕であって267ms停止または持続30fpsの保証ではなく、nominal 120 FrameでBackpressureが1件でも出ればQualification失敗とする。可変容量、動的調整、追加WorkerまたはPool数比較をPhase 0.11へ追加しない | D-137／D-142／D-143を補足。Phase 0／0.1、Publication／Recoveryは変更しない |
| D-145 | Phase 0.11 Ordered Worker並行動作と停止順 | Ordered Submit WorkerとOrdered Output WorkerはRun開始から同時に稼働し、Output WorkerはSubmit Workerのdrain／joinを待たず生成済みrecordをFIFO先頭から継続消費する。Main Thread CoordinatorもRun中からFrame Completionをbounded pollして正式状態へ反映し、固定8組を循環再利用して総数120 Frameを処理する。StopAccepting／`BeginDrain`後も両Workerは並行動作を続け、Submit Workerは全Accepted Workのrecordを各1件生成して先に停止し、Output Workerは残存record、D-147のowned Access Unit／同期Sink処理とCompletionをdrainする。Accepted件数Snapshotとrecord／Output／Sink／Frame Completion生成・反映件数が一致し、通常経路の未返却owned領域が0件になった後だけ停止前のOutput WorkerへFinalize／Abandonを要求し、必要なContext terminal resultの回収・登録後に同Workerを停止する。両Worker静止と通常資源ゼロ確認後だけTrace seal／Plan commitへ進む。共通`ICaptureEvidenceSession.TryJoin()`はBackend全体の最終joinのままとし、Submit停止確認、Finalize要求、terminal result回収はPhase 0.11内部の非公開lifecycle境界に限定する。逐次Worker実行、8件Barrier、Output join後の追加要求またはPoisonの正常Drain偽装を禁止する | D-142／D-144／D-147を補足。Phase 0／0.1、Queue数・容量、Worker数、永続Schemaは変更しない |
| D-146 | Phase 0.11 NVENC Run固定interop lifecycle | `NvencBringUpProfileV1`はCapture開始前の非クリティカルなRun初期化区間で、単一Encoder Sessionと固定8組のCapture所有NV12 Input Texture、Output Bitstream Buffer、Completion Eventを各1回だけ生成し、native Texture pointer取得、`NvEncRegisterResource`、`NvEncRegisterAsyncEvent`を各Slotにつき1回だけ行う。`enableEncodeAsync=1`、`enableOutputInVidmem=0`を固定し、全8組の生成・登録完了後だけ受付を開始する。途中失敗は取得済み資源を安全な逆順でrollbackし、4組への縮退、実行時再生成または再登録をせずUnsupportedとする。各Frameは既存SlotへGPU変換し、`NvEncMapInputResource -> NvEncEncodePicture -> Completion Event待機 -> NvEncLockBitstream(doNotWait=0) -> copy -> NvEncUnlockBitstream -> NvEncUnmapInputResource`だけを行う。待機とlock／copy／unlock／unmapはOutput Workerだけが行い、Main／Render／Submit Workerは行わない。正常Drainでは、全native call帰還、全Input unmap、全Bitstream unlock、Context terminal回収後にOutput WorkerがAsync Event登録解除、Input登録解除、Output Buffer／Event／Encoder Session破棄を各1回だけ依存関係の逆順で完了して停止し、その証拠を取得したMain Thread Coordinatorが非クリティカルな終了区間でUnity管理NV12 Textureを各1回だけ破棄する。Backend全体`TryJoin()`と資源ゼロはWorker停止とMain Thread側Texture破棄の両方が完了した後だけ許可する。Run中のTexture／pointer／handle不変性を要求し、再生成、device reset、handle不整合または安全な解放証拠欠落では動的修復せずFail FastまたはD-143のPoisonへ進む。Poisonでは不明なnative資源にもUnity Textureにも触れずprocess終了まで一括保持する | NVIDIA公式WDDM非同期手順をD-137／D-142～D-145へ最小統合。Worker、Queue、Pool、Schema、性能SLAは追加しない |
| D-147 | Phase 0.11 owned Access Unit／Chunk Sink境界 | Output Worker内部を、NVENC Event待機・lock・Run開始時に1件だけ確保した16 MiB固定owned領域へのcopy・unlock・unmap・Encode Sample Slot一括返却を行うOutput Collectorと、そのowned Access Unitを一方向移譲されてaccepted FIFO順append、checked ByteLength、streaming hash、Frame Relation、owned領域返却を同期実行するRun Chunk Sinkへ責務分離する。両者は同じOutput Worker上で直列呼出しし、第三Worker、Queue、credit、独自Drain／Joinまたは永続Schemaを追加しない。Access UnitはWork Token、CaptureFrameId、owned領域、valid length、返却authorityだけを持つprocess-localな非copy／一回消費Leaseとし、Artifact／Registry／Session／Freeze／H.264解析情報を持たない。Slot返却はcopy・unlock・unmap完了後の単一境界とし、Sink結果より先の再利用と、その間に後続encodeが進み得ることを許容する。Frame CompletionはSink成功／失敗結果後にOutput Workerだけがexactly once生成する。append／Relation／counter失敗では修復・truncateせずchunk全体を`Abandoned`、所有権不明ではbuffer返却やCompletionを推測せずD-143のPoisonとしてowned領域もprocess終了まで保持する | D-142／D-144～D-146を補足。将来の別I/O Worker、Queue、Completion方式またはPhase 4.8互換を保証しない |
| D-148 | pilot後のPhase 0.2成果物契約 | D-112～D-115およびD-128を置換する。Buildingは処理前に人間が選定したPPU豆腐型だけをEligibleとする方針を維持し、適格性・規則・除外理由をv2の`Phase02Eligibility／EligibilityRuleId／ScopeReason`へ記録する。旧Building専用識別子をv2の必須値として復活させない。5カテゴリ別Recipe、固定Blender／本隊移植Script、Geometry／Recipe／Role／Selection分離、tagged PlannedVariant出力、Entry単位provenanceとcohort、Catalogの全Source事前Triangle計数を禁止して通常Import後Reportへ置き、SourceFixtureIdを最大64文字、Manifestの割当hash名をBatchAllocationProfileContentSha256とする。CatalogをSource indexed root外へ置き、SourceBundleContentSha256で正確なSource Indexへ結合する。設計正本はDESIGNへ一本化し、candidate優先規則を廃止する。candidateはPhase 0.2完了時に削除する補助実施計画とする。Catalog v2の完全列挙EligibilityとRule Set／Entry Rule分離、Catalog Freeze時の全均衡batch Manifest一括確定、3種ordinalと7 stratum queue、完了frontier限定Report被覆、再生成を局所化する監査、最小quotaを10.2.2の正本とする。BuildingはGeneric Geometry＋4 Slab以上の薄いsidecar、Vehicleは1 cm Voxel、Convexは単一Hullだけ。Codec頂点上限255とPhysicsCookInput上限128を分離し、Face数は頂点数から導出する。正式pilot Geometry直接採用、全Licensed再生成、一般Convex分割、製品Strict Solidを要求しない。変更済みLicensed選抜schemaはv2、ZCG／Bundle／Synthetic Validator v1のbytesは維持する | 一部廃止。人間によるBuilding個別選定とBuilding測定の扱いはD-150へ置換し、Profile責務・Pilot資源条件・Licensed監査の具体化はD-151へ継承し、Source／保存基底の上限・Review専用BakeはD-155へ部分置換し、他の成果物契約は継続。Phase 0.2先行branchも本隊に含む。後続Runtime上限・製品Recipeを変更しない |
| D-149 | 剛体Local Plane実姿勢リベース | 19.5.1の自由飛行剛体だけ、予測Pose差の一致判定を世代・前提と実姿勢での面誤差Gateへ置き換える。攻撃SourceSlashPlaneは不変、命中前の仮Local Planeを命中時に一度だけ採用／Fallback確定し、7.6の受付判定と、受付済みのTemporary／Provisional／Stable／Finalへ共通使用する。面採否と受付判定はMesh／Collider Readyから独立し、未完成だけを理由に切断位置を変更しない。対象ごとの面差と固定点近似の未検出差を許容するが、Actor pose／速度を予測または命中Snapshotへ戻さず、Final包含・支持安全・世代検証を維持する | 確定。4.2／19.1／19.4／19.5の姿勢一致規則に対する限定例外。D-046のMob／Skinned契約は変更しない。O(1)直接予測式の標準採用とは独立 |
| D-150 | Phase 0.2 Building自動Eligibility | D-148のBuilding人間選定部分を置換する。10.2.2のphase02-building-v2／phase02-eligibility-v3で、正式adapterの元root＋descendantへ固定family・Coverage・Depth・寸法screeningを適用する。単一読み取り専用Blender 4.5.12 processによる限定preflightと判定記録を採用し、pilot assembly再構築・個別手動membership変更・生成Recipe実行を要求しない。偽陰性、開始前測定、判定不能1件によるCatalog Freeze全体停止を許容する。下流Geometry／Role GateとStructuralSlabFixture成立条件を維持する | 確定。軽量性はpilot確認済み。Catalog v2の形は維持し、旧Rule IDの意味を変更しない |
| D-151 | Phase 0.2割当・処理Profile分離と実測監査 | D-148のProfile責務を具体化し、独立EarlyFixtureBatchAllocationProfile v1だけをCatalog／Manifestから参照する。処理／評価／資源改訂は割当を変えずEntry単位cohortへ記録する。EarlyFixtureResourceProfile v1でPilotの一律4 GiB hard limitを撤廃し、計測値からExpansion上限を事前固定する。120秒＋resource要因だけ1回300秒retry・非並列・決定論的Geometry上限を維持する。Licensed少数監査は10.2.2の初期toleranceと論理親参照一致を正本とし、再生成Geometry hash一致を要求しない | 一部廃止。Pilot上限なしと実測によるExpansion上限決定はD-152へ置換する。Profile分離・監査tolerance等は継続し、全Licensed再生成・新性能SLAは要求しない |
| D-152 | Phase 0.2共通32 GiB Working Set上限 | D-151のPilot上限なし／Expansion上限探索を置換し、両BatchKindとBlind生成処理に32 GiB（34359738368 bytes）を一律適用する。120秒＋resource要因だけ最大1回300秒retry、Blender非並列、Geometry上限、ResourceDeferred分類を維持する。Peakは診断用であり上限探索・確定待ちを完了条件にしない | 一部置換。閾値32 GiBは維持し、メモリ指標・監視方式・観測schemaはD-153で具体化する。実機の余裕を前提としホスト全体の安全を保証しない |
| D-153 | Phase 0.2単一process監視と監査範囲 | D-152の32 GiBを単一Blender Working Setの定期監視による停止閾値に限定する。監視間・停止までの超過と対象外メモリを許容し、process tree commit制御・集計を要求しない。PeakWorkingSetBytesと観測statusへ統一しPeakProcessTreeCommitBytesをcanonical Attemptから削除する。D-151のLicensed監査再生成は正式Geometry／sidecarと対応Report Entry・選抜結果だけとし、Review専用UV／atlas／2048 bake／画像を除外する | 一部置換。従来維持したtimeout・証拠のあるOS資源不足のretry契約はD-154へ置換する。単一process監視・監査範囲・初回Human Review・正式Geometryの必要処理は維持する |
| D-154 | Phase 0.2自動resource retry対象の限定 | D-151～D-153のresource retryをTimedOutと観測済み32 GiB Working Set超過のMemoryLimitExceededだけに限定する。初回120秒＋同一Resource Profileの最大1回300秒retryを維持する。それ以外のOS資源不足はToolFailed・自動retryなしとし、新しい障害enumやOS固有判定を追加しない。最終Attemptの結果で終端Statusを決める | 確定。一時的なOS資源不足でも自動retryしないことを許容する。後の明示的再実行は既存記録を保持した新実行／cohortとして可能 |
| D-155 | Phase 0.2 Source／未削減基底の正式保持 | D-148の一律200000 Triangle制限をSource／許可保存基底だけ2000000へ置換し、正式ZCG・親参照を保持し、Dataset収録は選抜された子に必要な既存Binding親closureだけとする。通常Variant／RenderCutInputは200000、Synthetic／Convex／Runtimeの上限は維持する。保存基底のZCG読込み・canonical encode・保存・再読込みの全境界で128 MiBまで許可しbounded検査を維持する。未削減基底のReview専用Bake／UV／atlasは任意、全11方向画像とHuman Reviewは維持する | 一部置換。Review表示方法とSelection Profile identityの具体化はD-156へ継承する。保存・検証・監査の追加負荷を許容し、Resource上限とtimeoutは増やさない。Voxel基底非保存・複合Recipe化、任意Dataset membershipと新Review表示modeは採用しない |
| D-156 | 未削減基底のReviewとProfile最小具体化 | D-155のReview表示mode追加禁止を置換し、基底は面数によらず利用可能な既存Material／Textureを使い、不足slotだけNeutralとする。新規UV／atlas／Bakeは行わず既存11方向画像対を維持する。Review記録だけに実際のappearance区分を追加し、通常GeometryのBakeと全Geometry選抜契約は維持する。2M上限とCategory×Recipe表は既存Selection Profile v2へ統合する | 一部置換。slot別appearanceと不完全root定義はD-157へ置換する。別上限Profile、新Artifact、Dataset membership例外、Texture修復workflowを追加しない |
| D-157 | Phase 0.2 Review固定分岐とProfile完全列挙 | D-156のslot単位分類を廃止し、Character／Environment／PropsのOriginalはImport済みappearance（MaterialなしはNeutral）、Building／Vehicleの保存基底はNeutral、通常GeometryはBakeへ固定する。MixedAppearanceを削除し参照Texture障害はPresentation失敗とする。Selection Profile v2は全11 root propertyと既存Preset参照を完全列挙する | 一部置換。Presetの参照方式はD-158で直接参照へ限定する。新しいArtifact／Dataset規則、Material修復、追加画像Suiteを要求しない |
| D-158 | Phase 0.2 Preset直接参照への縮小 | Selection Profileの全11 rootを維持し、Generation／GateはRecipe／Gate Presetを直接参照、Selection／Auditは各1設定文書とする。共通Rules wrapper、RuleKey、Ruleごとの実装／Configuration hashとvalidator結合を廃止する。exact Preset Bundle内のpath／hash確認と専用Loaderを使い、実装identityは既存Script Bundleで保持する | 確定。Review、2M保存、128 MiB入出力、Dataset親closureを変更しない。汎用被覆検証やRule単位Goldenを追加しない |
| D-159 | 可変長Trace移行 | Phase 0～0.11は既存固定長Trace仕様で完了判定し、Phase 0.12～0.14を同一移行系列の内部checkpointとして、0.14成立時に一度だけ可変長Traceへ切り替える。新runtimeはproducer専用の固定容量Payload RingとRuntime Index、事前確保Paged History、単一`trace.bin`の逐次形式を使う。永続Index、二ファイル形式、`RecordVersion`、Context Registry、Crash Recoveryを追加せず、既存bundle directory公開をv2へ更新し、v1 Loaderだけを維持する | 確定。詳細は21.16。旧新backendのRelease並行搭載、二重記録、v1再export、電源断durabilityを要求しない |
| D-160 | コミット後の任意分割・物理GC | 7.9を正本として、確定後の単一平面による通常物体2個への追加分割と、共用面集合0の物理所有単位の寿命終了を独立した任意機能とする。既存BackgroundMaintenanceと非命中公開・退役を使い、追加分割は全体1未回収試行と入力別不成立抑止で管理する。通常切断・過去Commitを救済対象にせず、実行不能なら元物体を残す | 人間承認済み、2026-09-08。Phase 5.6／5.7は省略可能。許容事項は7.9.6、最小確認は7.9.7。実装・実測済みを意味しない |
| D-161 | 実行時表示表現と描画ロードマップ | 4.5の読み込み時Mesh、切断時VP、CPU AoS／Index正本とGPUコピー、範囲所有権、Published Vertex追記、退役Index再利用を採用する。通常切断は単一Index予約へ正負を直接配置し、物理採否に依存する再集約を行わない。必要転送と現在の参照・frameで表示公開し、CPU読取り公開・再切断・物理適用とは分離する。Phase 0.9～0.94でStage 2まで比較する | 人間承認済み。準備・変換の表示開始負荷、GPU拡張STW、容量限界での開始拒否／終了を許容する。Stage 2高速化は必達でなく、効果が乏しければStage 1を採用できる。Phase 5.6のIndexコピー・新旧共存費用を許容する。実装・実測済みを意味しない |
| D-162 | 未来予測用SkinnedMesh Jobベイクの採否判断 | 4.5.2を正本として、Phase 4.65で不変Rig Poseから共通VP入力を生成する限定実装を同期経路と比較し、人間が導入の採否を決める。採用時だけ既存DAG／VPプールへの本体接続、4.7の未来VP準備、5.1の人形先行切断統合を行う。通常命中の同期BakeMeshと通常SkinnedMeshRenderer描画は維持する | 人間承認済み、2026-09-09。効果がなければ導入見送りも4.65の正常完了とし、人形の先行準備による命中時負荷削減を必達にしない。対応範囲の限定と非bit一致は4.5.2に従う。総時間短縮・翌フレーム完成は保証せず、5は採否待ちにしない。実装・本体実測済みを意味しない |
| D-163 | 物理Convexの内接頂点削減 | 7.2を正本として、Runtimeの1 Convex上限L=128を入力登録と最終出力に適用し、超過出力だけ今回生成した交点を局所削除する。統合後も継承頂点を保護し、残存座標・内接性・形状妥当性・決定性と探索を伴わない終端を維持する。次数3＋ΔV順はPhase 4の初期実装方針とし、唯一の製品合格方式には固定しない。削減後形状を質量・cook・再切断の共通基底とし、行き詰まりは既存物理正式採用へ閉じる | 人間承認済み、2026-09-09。接触消失・累積縮小・物理的隙間・表示張り出しへのNo-op・質量特性の変化を7.2の範囲で許容する。不変条件を満たす削除順の変更による採用形状・体積損失・接触・質量配分の差異も許容し、実装変更をまたぐ同一形状は要求しない。全入力での上限到達や損失最適性は保証せず、親質量保存・形状妥当性・Final包含を維持する。Phase 4で実装・検証し、実装済みを意味しない |
| D-164 | 正負二集合と直接Index出力 | 通常切断は正負各最大1所有者、固定は点Anchorの有無、所属はSideのConvex集合の正常な空／非空で決める。通常Indexは一つの予約へCapを含め正負連続出力し、新規転送1回・既存GPU範囲再利用時0回とする。島の独立化とIndex振り分けコピーは任意Phase 5.6へ置く | 人間承認済み、2026-09-10。一体運動・空中浮遊・接着情報喪失・遅延分離・分割不能・Bounds拡大・倒壊、支持による仮描画省略をしない費用、任意分割のIndexコピー／新旧共存を許容する。完全分離・倒壊防止・速度改善を必達にせず、幾何Topology・質量・資源寿命・一般物理Fault処理は維持する。旧機能は§17の限定例外で削除し、実装完了を意味しない |
| D-165 | Phase 4.3 建物World D6と一般外部Joint撤去 | 7.2.2を正本とし、建物由来の動的な1→2物理分裂子へ独立World D6を一つ生成する。垂直並進Free、水平並進・全回転Limited、3値によるDepth別指数Limitを使う。一般外部Jointの継承・付け替え・GC保護・予測を撤去する。建物は製品の切断対象、道路は非対象とする（O-005解決）。Runtime本体は独立Phase 4.3、製品Recipeは5.5、任意分割・GC統合は5.6／5.7 | 人間承認済み、2026-09-10。7.2.2の品質・運動・費用・製品入力制限を許容する。Depthは建物由来のProvisionalを含む実際の1→2公開時に一度だけ進め、非建物の系譜はfalse／0を維持する。Final採否で再加算・巻戻し・基準姿勢リセットをしない。未対応Constraint付き動的近傍が必要な局所予測は後追い処理へ送る。実装・拘束効果の検証済みを意味しない |

## 13. 未決事項

| ID | 論点 | 選択／質問 | 影響 | 決定時期 |
| --- | --- | --- | --- | --- |
| O-001 | 初期ターゲット | 解決済み：PCVRを採用（D-011） | Quest単体は当面スコープ外 | 2026-08-21 |
| O-002 | 目標FPS | 解決済み：両眼描画90fpsを基準（D-012） | 再投影は安全網として扱う | 2026-08-21 |
| O-003 | Temporary Renderer上限 | 同一物体の`TemporaryRenderCapRecordSet`について、固定側も含む実Cap 2、3、4枚のどれを標準上限とするか | 描画コストと連続斬り感 | T-003後 |
| O-004 | 断面表現 | 共通トゥーン＋粘土色グレーは確定。機械内部や人体で追加記号・部品表現を使う範囲 | 年齢区分とアート制作 | アート検証時 |
| O-005 | 切断可能範囲 | 解決済み：人間判断により建物は切断対象、道路は非対象（D-165） | 建物対応をロードマップに含め、道路切断は実装範囲に含めない | 2026-09-10 |
| O-006 | 破片寿命 | 最大動的破片数、消去時間、スリープ規則。7.9の確定空物理GCとその接触・質量消失の許容は確定し、巡回頻度・製品予算・効果の実測は実装時に決める | 物理CPUと視覚密度。GCへのSleep必須化や期限保証はしない | T-010後／任意Phase 5.7 |
| O-007 | Collider仮状態 | 旧Collider維持時間と周辺破片の例外判定。Player Body／Handと刀は物理接触せず、刀は論理Sweepのみ | 違和感と実装複雑度 | T-005後 |
| O-008 | NPC構成 | Synty人物をそのまま使う範囲と顔・体型改造量 | 独自性と制作工数 | アート検証時 |
| O-009 | データ保存 | 切断状態をセーブ対象とするか | 再現性・容量・ロード時間 | ゲームループ決定時 |
| O-010 | ネットワーク | 将来的なマルチプレイ要否 | 切断イベント同期設計 | 企画判断 |
| O-011 | Trace保存量 | リングバッファ秒数、最大イベント数、書き出し形式の最終値 | メモリ、調査可能時間、ツール工数 | T-020後 |
| O-012 | Voxel品質 | Asset分類別のVoxel Size、Adaptivity、穴封鎖閾値 | 輪郭精度、面数、処理時間 | T-022後 |
| O-013 | 建物分割 | Structural Slabの標準寸法、入口用Compound分割、点AnchorのRecipe指定方法 | 局所切断性能とアート破綻 | T-024とPhase 4／5.5 |
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
| O-032 | 最終重力と周辺調整 | 0.35G／0.5G／0.7G／1.0Gの採用値と、反発、Drag、分離Impulse、Animation、破片寿命の追加調整要否 | 空中斬り成功率、世界の重量感、テンポ、物理安定性 | T-064のプレイテスト後 |
| O-033 | Shadow近似品質 | 両面・キャップなし近似を許容する距離／時間、Stable専用Shader分離、問題時の簡易Shadow Cap導入条件 | Shadow GPU時間、Draw、接地影、Self Shadow、実装複雑度 | T-065後 |
| O-034 | Stencil Batch予算 | `MaxStencilColors`、OBB／Cap Bounds Margin、World Plane一致epsilon、Facing epsilon／ヒステリシスを決める。Count方式は128初期化の正符号8bitへ固定し、相殺・Color超過の救済条件、距離別Cap省略、別Backendは追加しない | CPU分類・Color割当て時間、Stencil GPU時間、Draw、最後の統合Color比率、仮断面品質 | T-066～T-068後 |
| O-035 | Job実行予算 | フレームごとのSchedule数、Batch Size、Worker占有上限、複数フレームJobのNativeメモリAllocator／寿命、表示VP更新・公開数、物理MeshData一括Commit数、Bake同時実行数 | 90fps安定性、投機完了率、Pending滞留、メモリ | T-069／T-076後 |
| O-036 | Native Cook再検討閾値 | 「継続的に大きい差」の倍率、Unity Bake P99／Pending許容時間、Worker占有、Native部分置換へ進む最低改善量と保守工数上限 | Backend選択、実装規模、Unity更新追従、再現性 | T-070／T-076を比較できるPhase 4.1完了後 |
| O-037 | Surface Projection研究条件 | Trusted Exterior分類、最大距離、法線内積、包含Margin、最小厚み、Reduction前後の再Projection条件、自己交差検出精度 | Silhouette回復、Solid堅牢性、自動成功率、前処理時間 | T-071研究を開始する場合だけ |
| O-039 | Geometry／Cook容量予算 | P95／P99容量式から、1フレーム当たりWorker時間、Deadline別の同時切断数、`MaxIncompleteCutOperationCount`、Temporary Renderer上限、Batch Size、同時Bake数、Temporary Physics Proxy上限を決める | 先行計算完了率、命中後Pending時間、90fps安定性、受付見送り頻度 | T-076後 |
| O-040 | Player壁境界応答 | 禁止Occupancy侵入時にPlayer Rootを押し戻すか、侵入深度を増やす移動だけ拒否して退出を許すか。finiteかつ0以上の`occupancyExitEpsilon`、ExitSearchMaxHorizontalExpansion、MaxExitVolumeCount、MaxExitCandidateCount、ExitLineSearchSteps、ExitBlockedFixedStepLimit、MaxForcedOverlapFixedSteps、Near-Wall Fadeの開始距離と強度 | VR快適性、壁抜け、Fixed Step予算、予測単純性 | T-088プレイテスト後 |
| O-042 | 製品Structural Slab自動化 | 早期sidecarのTransform／OBB／外周順とは別に、入口認識、点Anchor、厚み調整を共通PresetとAsset Recipeのどちらへ置くか | 製品品質、前処理工数 | Phase 5.5。早期sidecar契約を未決へ戻さない |
| O-043 | Hybrid Clip予算校正 | Raster 8面を固定したままPixel fallbackを0～4面のどこへ置くか、Stable専用Shader分離、Ignored境界が見える最長時間とGeometry Job優先度 | GPU時間、MSAA edge品質、Shader register／varying、連続斬り品質 | T-089後 |
| O-044 | Provisional Physics Profile | MaxProvisionalActor／ShapeInstance／Constraint数、2 Slot × MaxProvisionalGroup分のGroup Snapshot固定容量、Provisional警告／Fallback開始時間、分離距離、法線再侵入Limit、`FinalContainmentEpsilon`、異常線速度／角速度、D6対Custom Constraint、Actor／Joint Pool導入閾値 | 生成／破棄CPU、Snapshot更新時間／メモリ、Broadphase、Solver時間、Ghost Contact、接触Impulse、連続切断、handoff品質 | T-091後 |
| O-045 | Mob軌道Cache Profile | Crowd StepのFixedStep倍率、Tier別Horizon／Refill閾値、最大Mob／Sample数、同時再計画Group数、Live Fallback予算、Hold許容時間、将来のGrid Cell／MaxNeighbors／ORCA Horizon | CPU、Nativeメモリ、Queue枯渇率、停止時間、重なり、先行切断Commit率 | T-092の計画単体結果で初期設定を決め、先行切断Commit率による判断はJobベイク採用時のPhase 5.1の統合結果へ遅延し、不採用時は要求しない。4.7の完了を塞がず、ORCA値は追加導入時だけ確定 |
| O-046 | Animation Pose Evaluator | controllerなしPlayable／MixerとRetarget済みPose Tableの採用、Rig Pose Buffer形式・Bone順、Source Timeの数値精度、Main Thread／Job Batch予算、2 Source BlendおよびimmutableなLook／IK Layer入力の導入時期 | Pose誤差、Main Thread時間、Job Throughput、Pose Tableメモリ、Humanoid Retarget品質、先行切断採用率 | T-018後。人形の先行切断採用率による評価はJobベイク採用時のPhase 5.1とし、不採用時は要求せず、4.6の完了を塞がない。Loop／ClampとSource Timeの意味契約は未決にしない |
| O-047 | 剛体リベース許容値 | RigidCutRebaseProfileV1の法線角度、Bounds内plane field差、左右眼ProxyPoint pixel差の閾値を少数の距離／サイズ／斬撃例で校正する。8点近似を真の切断線最大誤差としない | 投機再利用率、切断縁とVFXのズレ、VR知覚、命中時CPU | T-093／Phase 4.5。未校正でも有効な試験Profileを明示し、製品値の未設定を無制限許容にしない |
| O-048 | Building World D6設定 | L1、A1、共通減衰率rと、建物D6を含む既存Constraint容量の製品値 | 拘束挙動、Joint数、生成・退役・Physics Step費用。倒壊防止率・最大変位・性能SLAは追加しない | Phase 4.3／T-094の少数FixtureとProfilerで判断 |

## 14. 技術検証項目

Phase 5.6／5.7の任意機能固有の確認は7.9.7へ集約し、下表の既存試験は担当契約の範囲で再利用する。既存Phaseの完了条件へ追加機能を前倒しせず、専用Test IDや大規模性能matrixを追加しない。

T-027～T-030は、Phase 0.2で明示済みまたは後続Phaseで採用した前処理だけに適用する。試験を満たすために未採用の修復方式を実装せず、Phase 0.2の既存Recipe・Gate・選抜条件を変更しない。

| ID | 対象 | 合格の考え方 | 方法 |
| --- | --- | --- | --- |
| T-001 | 斬撃検出 | 高速な刀でも切り抜けず、一意な切断面が得られる | 速度別1000回で欠落率と重複率を計測 |
| T-002 | 即時分離 | 必要なベイク・VP変換後に仮分離が視認できる（4.5.2） | GPUタイムと入力から表示開始までのフレーム・残る準備費用を記録し、代表入力で4.5.2の同フレーム表示目標とフレーム全体の負荷を確認する |
| T-003 | 複数Pending | 2〜4切断で画質と性能が許容範囲 | 切断数別にCPU/GPU、Draw、overdrawを比較 |
| T-004 | Stencil断面 | 正常な正向きの共用Geometryで、5.2の明示的品質例外を除き穴・はみ出し・片眼ずれがない | 箱、凹形、人形の共用Geometryを通常の外部視点で両眼確認する。符号・Color・Camera・Plane overflowの例外はT-066／T-067／T-088／T-089で確認し、この試験へ重複展開しない |
| T-005 | Convex切断 | 7.2の上限内の閉凸出力を生成し、同節の接触変化の許容と既存の物理Commit条件を満たす | 少数合成Fixtureで上限内は削減なし、有効なL以下入力を切って129頂点以上になる削除可能例は128以下へ削減する。2回以上の削除を含め、重複統合後の継承頂点の保護、残存座標不変、閉凸性・内接性・非退化性と同じ実装・入力条件での決定性を確認し、特定の削除順との一致は合格条件にしない。候補不足は既存終端・回収、表示張り出しへのNo-opは許容結果として確認する。完了時間・接触変化・速度継承を既存計測で確認し、成功例を既存物理正式採用だけで代替して合格にしない |
| T-006 | 共用Geometry切断 | 断面が閉じ、UV／法線／submeshが保持され、同じ結果が表示／Stencilへ使われる | 代表10プロップを多方向に連続切断 |
| T-007 | 世代競合／公開前受付制限 | 先行Operation未公開の親と仮表示領域への後続切断を見送り、Operation公開後はGeometry／cook／Physics未完了でも、4.5.3のCPU読取り公開で確定子を再切断できる。GPU転送・集約完了を待たない。終端失敗した未公開Operationの後着成果物を公開しない | AのOperation公開を遅らせ、仮表示領域へのBが状態、世代、未完了件数、Aの仕事を変えず見送られ、A公開後もBが再生されず新しいCを受付けることを確認する。別FixtureではAをOperation未公開のまま既存規則で終端失敗させ、A由来のPending Cut／仮表示だけが退役し、親の現在有効な共用Geometry、A以前の未Commit祖先制約、履歴、物理姿勢／速度が残ること、未完了件数が安全な回収後に一度だけ減ること、最新世代でCを受付けること、世代一致のA後着結果でも状態が戻らないことを確認する。無関係な確定済み対象はAの公開待ち中も受付可能とし、A公開後のCと既存Jobの完了順を反転して従来のGeneration Rejectも維持する |
| T-008 | Skinned切断 | 姿勢固定から静的破片への切替が見えない | 歩行・走行・腕振り中に各部位を切断 |
| T-009 | 入力モデル耐性 | 契約内モデルは自動前処理で切断可能になる | 変換検査とエラーレポートを確認 |
| T-010 | 破片予算 | 連続プレイでCPU／メモリが上限内へ収束 | 10分間の連続切断ストレス試験 |
| T-011 | XR描画 | Single Pass環境で両眼のclip／Stencilが一致 | 左右眼スクリーンショットと実機確認 |
| T-012 | Collider cooking | バックグラウンド化後にメインスレッドスパイクが残らない | Profilerで切断前後フレームを追跡 |
| T-013 | 非VR性能基準 | 同一負荷を自動再生し、変更前後を比較可能 | 固定カメラ、固定乱数、切断スクリプトで計測 |
| T-014 | Quest Link XR | Quest 3S有線接続で両眼表示、追跡、90Hz、Single Passが成立 | HMD内目視とProfiler計測 |
| T-015 | 斬撃波先行切断 | 接触前の完了率が即時レンダラ負荷を有意に減らす | 距離、速度、対象数別に事前完了率とPending時間を測定 |
| T-016 | 未来評価器統合 | DAGがReady Work ItemをV1 Dispatcherへ渡し、未Schedule取消、Schedule済みJobの世代不一致破棄、Commitを競合なく行う | 遅延、進路変更、再切断でPriorityClass／Deadline順を意図的に反転し、T-090のQueue単体契約と統合する |
| T-017 | 解析／局所物理予測 | 自由飛行のO(1)直接予測と、接触等を扱う局所Prediction Physicsを分離し、各経路が本体状態と正しく統合される | 固定Unity版で、直接予測のSnapshot、重心／Actor原点、回転、重力Profile、FixedStep境界、適用条件外Fallbackを確認する。局所Physicsは19.4で適用可能な接触・転動対象について姿勢誤差、採用率、予測CPU時間を測定する。直接／局所の各入口で既知の建物D6／Provisional Constraint付き対象を除外し、局所予測に必要な動的近傍だけが未対応Constraintを持つ代表caseでも候補を後追い処理へ送ることを確認する。剛体面リベースはT-093で別に検証し、Probeの測定値を製品保証にしない |
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
| T-054 | Unity選択的録画 | 片眼映像、異常前後リング、限定静止画がFrameId／Traceと一致し、録画有効時も性能予算内 | 最新のTier C Phase 0.11 qualification成功を入口証拠とし、Phase 0.11ではWDDM非同期NVENC、固定2 WorkerのAccepted FIFO順submit／Output回収、reorderなし、Capture専用のSource／GPU同期／Encode Sample／Work固定8 in-flight、固定memory streaming verification、Fresh final全file hash 1回、CaptureCompleteでのReceipt再利用、Main／Render Thread非待機を前提にする。Phase 4.8で解像度、30／45fps、リング長、正式chunk長、GOP、Container、segment、durability頻度、index、seek、保持期間別のGPU／CPU時間、Dropped Frame、保存遅延を比較する。上限を外す場合はRegistry／Publication Planの計算量、payload copy／hash回数、停止時Publication時間を先に実測・改善し、必要な場合だけ代替EncoderまたはCapture経路を再評価する。Phase 0.11のchunk形式を製品用連続録画形式へそのまま昇格しない。T-054はPhase 0を再オープンせず、Phase 0.1／0.11のTier A～DへPhase 4.8の性能matrixを混入させず、両Phaseの完了条件にも含めない |
| T-055 | OpenXR Projection Capture | D3D11固定ProfileでRelease前CopyがSwapchain所有権、Texture Array、左眼SubImage Rect／Array Indexを正しく扱い、提出画像を破損しない。MSAA、別API、想定外LayerはFail Fast | 正常Profileで非録画時との画像・Frame timing差を比較し、MSAA、D3D12、別Array Size、追加App Layerを故意に与えて録画停止とTrace理由を検査 |
| T-056 | Capture相関と限界 | predictedDisplayTime、Pose、TestRunId、ゲーム内ID、画像が一意に対応し、Projection正常／最終HMD異常を区別できる | 意図的な描画不具合、Dropped Frame、Reprojection、Link品質低下を発生させ、Unity Capture、API Layer Capture、HMD観察を比較 |
| T-057 | Capture環境識別 | Runtime／Driver／Swapchain等が変化したRunを別環境として識別し、固定Profile逸脱を見逃さない | Driver、Meta Runtime、Render Scale、Refresh Rate、Unity Packageを個別変更し、Run Manifest差分と比較拒否を確認 |
| T-058 | Pending物理共有 | cookを意図的に遅延・失敗させても切断表示が同フレームに始まり、共有Collider中のめり込みと透明接触が許容範囲に収まる | Bake遅延を0～数秒へ変え、表示開始フレーム、分離量、接触差、Timeout品質低下、後続切断を測定 |
| T-059 | 物理分裂Commit | FragmentGroupから左右Rigidbodyへの切替で位置・速度が連続し、Solverによる大きな跳ねやメインスレッド停止がない | 並進・回転・接触中にCommitし、重心速度誤差、Impulse、主スレッド時間、視覚フレーム差を記録 |
| T-060 | 二段階Cooking | Fast Cookで物理分裂を早め、選択的Fast Simulation昇格が再cook費用を上回るPhysics CPU削減を得る | Fast Cookのみ、Fast Simulationのみ、二段階を同一切断Traceで比較し、Bake時間P50／P95／P99、Pending時間、Upgrade率、10分間のPhysics CPUとピークメモリを測定 |
| T-061 | Collider Upgrade Commit | 同形状の別Meshへの差し替えで位置・速度と接触が連続し、再切断済みの古いUpgradeが適用されない | Sleep、自由運動、接触中、同時再切断を再現し、Wake、接触Impulse、主スレッド時間、Generation Reject、Mesh回収を確認する。内接削減済みの採用形状を再cookへ渡し、削減前へ復元されないことも確認する。接触中延期は7.3を維持する |
| T-064 | 全体低重力プレイ | 一般プレイヤーが空中物体を狙いやすく、世界全体の浮遊感とゲームテンポが許容でき、全軌道系で重力が一致する | 0.35G／0.5G／0.7G／1.0Gを同一投擲・切断Scenarioで比較し、滞空時間、斬撃成功率、主観評価、Physics／予測／VFXの軌道差を記録 |
| T-065 | 即時切断Shadow | Stencil Capなしの両面Shadowが即時状態で許容でき、clip／Offsetがカラー像と一致し、片面／両面群分割が90fps予算を阻害しない | 箱、薄板、凹形、非閉形状を床／壁近傍で切り、単一Directionalの各Cascade、Bias条件について実Capとの差分、漏れ、peter-panning、Shadow Draw、GPU時間を比較 |
| T-066 | Stencil Color割当て | 通常Colorでは左右眼いずれかでResidual Stencil Supportが重なる非互換対象を分離し、実行Color数を`MaxStencilColors`以下に保つ。配置できない対象は最後のColorへ入り、各Colorで全Volume後に全Capを描く | 左右眼だけでCapが重なる配置、OBBは重なるがCapは非交差の配置、全Cap重複、非重複、小さいColor上限を確認し、CPU分類、Color数、統合Color比率、Clear／Volume／Cap GPU時間、Drawを測定する。全Graph／全Edge、特定のColor番号、方式間で同じ彩色結果、統合Colorの画像正解を要求しない |
| T-067 | 正符号Stencil／互換Group | 128初期化とWrap加減算から得る`S>128`が範囲内の`W>0`と一致し、共通契約を満たすGeometryを符号保存のまま共有できる。通常Colorでは非互換Residual Supportの分離条件を守る。描画結果には5.2の明示的品質例外を適用し、最後の統合Colorでは非互換対象の分離を要求しない | 正向き箱、凹形、同方向重複と、生のCount `-1／0／+1／+2`が`127／128／129／130`になる小さいFixtureで`Ref 128 / Comp Less`、Read／Write Mask、IncrementWrap／DecrementWrap、Color／Depth writeを確認する。全体反転閉Mesh、正逆重複、別TopologyのCoincident／Nested／Self-intersection、負determinant Transform、World Plane／Material差、左右眼を試し、向き正規化や二重Transform補正がないことを確認する。範囲外Winding、入力Gateの不合格行列、削除済みの符号証明、向き正規化、Winding上界、Count容量分割、符号別Groupをこの描画試験へ追加しない。8bit排他不能構成はゲーム開始を拒否する |
| T-068 | 両眼Cap可視性Cull | 両眼とも裏向きの互換Groupだけが安全に早期除外され、片眼可視、面近傍、正負破片でCap欠落や点滅を起こさずStencil仕事を削減する | 左右眼でFacingが一致／不一致となる配置、面横断、頭部微動、正負Cap、Frustum外を再生し、Cull判定、ヒステリシス、Stencil Draw／GPU時間、左右眼画像差を比較 |
| T-069 | Convex Job Pipeline | Convex分割と複数`Physics.BakeMesh`がメインスレッドを停止させず、世代不一致成果物を適用せず、Pending物理共有から安全に分裂できる | 破片数、面数、同時Slash数、Fast Cook／Fast Simulationを変え、各Job段階時間、Schedule数、Worker占有、Main Thread Commit時間、Bake P50／P95／P99、Generation Reject、物理差し替え時Impulseを測定。同一Mesh同時Bakeを不変条件として検出する |
| T-070 | Unity／Native Cook Probe | U1／N1／N2／N3を同一入力と近似条件で再現測定し、Unity経路の実費用、Hull再計算の寄与、完全Topology／直接生成の改善上限を工程別に説明できる。製品Geometry完成前の早期Probeであり、T-076の前提ではない | Phase 0.25では4／16／64／128頂点の公開PhysicsCookInputと最大128頂点のLicensed単一Hullを用い、129／255頂点Codec FixtureをCook入力へ流用しない。単発／Batch、Fast Cook／Fast SimulationをRelease相当で反復し、P50／P95／P99、Throughput、各工程時間、Thread占有、メモリ、失敗率、出力形状、接触／Query品質を測る。Target×Stage×ExecutionMode許可規則に従い、単一DatasetCaseIdと固定規模軸を持つ各系列のManifest／Resultを作り、Suite Indexでhashと件数を固定する。N1のHullComputation、N1／N2のPhysXFormatBuild／StreamSerialize／StreamLoad、N3のDirectInsertionを独立系列として復元でき、版違いと非利用可能なNative生成物を明記する |
| T-071 | Global Solid Reconstruction研究 | Voxel／SDF Union、内部充填、Surface Projectionから自己交差のないGlobal Solidを再構成できるかを将来研究する。製品Phase、代表Asset合格条件、Fallbackには使用しない | 開始時期未定。研究を開始する場合だけ独立DatasetとArtifact Schemaを新設し、標準Closed Component／Stencil／Compound Convex経路へ影響しない比較として実施する |
| T-072 | 固定物体の即時切断 | cook遅延中もAnchorを持つ所有者全体が固定され、Anchorなし側だけが仮分離する。固定を理由に仮描画を省略しない | 単一・両側・OnPlane Anchor、同Sideの離れた島、連続切断、先行結果Reject、cook遅延／失敗を少数例で確認する。固定側の誤Impulse・変位がなく、全体固定による浮遊とAnchor喪失後の大型物体の落下・回転を許容する |
| T-074 | 点Anchorと論理切断公開 | 点Anchorの直接継承、正負の論理子・切断面参照・世代の原子的公開を確認する | 受付時はPending Cutだけ、通常処理で確定後に子1／2件・実在境界を公開する。OnPlane両側継承、子の再切断による他方集合の不変、非identityなlocal frameを確認する。子0／3件、境界上限超過、重複・未知参照・世代不一致は部分公開しない。切断履歴と有効なOperation作成Traceを復元し、欠落・重複・件数不一致・未完了束を成功根拠にしない |
| T-076 | Geometry／Cook Microbenchmark | 製品のCPU側の共用VP Geometry切断、Convex切断、T-077検証済みTemporary Physics Proxy生成、`Physics.BakeMesh`を工程別に再現測定し、単発レイテンシとJob定常処理容量を分離して、入力規模からP95／P99完了時間を見積もれる。T-070の早期Probeを製品入力分布から補完・再解釈する | 公開合成DatasetをRelease Player相当／Burst有効でWarm-up後に反復する。計算KernelのSingle-Thread µs/op、Bake／Commitの直列単発Latency、Job Batchのcuts／triangles／convexes／cooks per second、Schedule／Complete latency、Worker占有、Main Thread Commit、GC／Nativeメモリ、失敗率を規模別に保存する。Target×Stage×ExecutionMode許可規則、Metric／Unit組合せ、系列一意性を検査し、`ColliderCommit + SingleThreadKernel`と`PlaneClassification + MainThreadCommit`をRejectする。ManifestのDatasetCaseIdと全規模軸をResultへjoinし、Samples／P50／P95／P99と容量式の説明変数を一意に復元する。同一Suiteへ同じDatasetId・異なるDatasetContentSha256を持つLatency／Throughput等を混在させたFixtureをjoin前にSuite Rejectし、別Suiteまたは別DatasetIdなら受理する。Bytes／Count Samples `[1,2]`からMean `1.5`を取得順binary64左畳みで再計算し、101件以上のPercent系列でもCountを範囲違反にしない。対象処理の失敗がRejectedではなくFailureRateへ入ること、一部計測不能時の件数、全試行計測不能時のSuite Rejectを検査する。Manifest／Result相互ID・hash・件数、Aggregate再計算、Result差し替え、欠落／余分Entry、開始／終了clean検証、途中HEAD変更、Repository外一時出力、Index-last原子的確定、未知Schema／property Rejectを試験する。Manifest 64 KiB、Result 64 MiB／100万Sample、Index 64 MiB／10万Entryと呼び出し側のより小さい上限、宣言件数超過、非seek入力、過剰nesting、末尾dataを配列確保前にRejectし、全Loaderに無制限APIが存在しないことを確認する |
| T-077 | Temporary Physics Proxy正しさ | Temporary Physics Proxyが有限で決定的なGeometryを生成し、watertight、面向き、凸性またはCompound規約、PhysX上限を満たす | 中央／端／非交差、薄形状、極端なAspect、複数Fragment、退化Bounds、NaN入力を合成し、同一入力Hashからの出力一致、有限頂点、Bounds逸脱、切断側分類、体積、凸性、Primitive重複、上限、Validation Reasonを検査する。不正結果は物理へ公開せず、合格実装だけをT-076へ渡す |
| T-078 | 早期Fixture選抜・再現性 | 5カテゴリ別Recipe、均衡batch、用途別Bindingとfrontierから下流入力を再現できる | 10.2.2のBuilding限定preflight・監査・再利用を検査し、全Source事前計数なしでCatalog／Manifestを確定し、通常Import失敗を当該SourceのReportへ終端記録して他Sourceを続行する。全カテゴリの小Pilot、最低12 unique Source／Render5帯／PhysicsCookInput6 Source／Building2 sidecarのquota、固定順位、一括Manifest確定後の再開／同一frontier、Eligibilityと結果の分離、Resource終端、再開時の完了Artifact再利用、Reviewによる直接membership変更禁止、cohort別再評価を検証する。Licensedは各カテゴリ最低1 SourceかつEnvironment Tree／Rock両方の固定監査sampleのGeometry／sidecar・対応EntryだけをReview専用bake等なしでclean再生成し、事前toleranceの意味的等価を確認する。全Licensedの2回生成は要求しない。公開Synthetic／Goldenは2回のbyte一致、非公開Dataset／対応表／画像の漏洩防止を確認する |
| T-079 | Early Fixture Reduction Variant | 許可カテゴリ／Recipe内だけで1回Ratio、Target／Actual、NoOp／Aliasを固定する | Target以下と1 Triangle超、Targetから外れた出力、異Target同一hash、先行canonical Artifact参照、自己／前方／別Source参照拒否を検証する。CharacterのDirect／Voxel非生成、BuildingのOFF／ON各親からのN-tri、Environment／Propsの増量禁止、Blind派生のBenchmarkOnly制限を確認する。NoOpは直接入力参照、Aliasは既存Geometry参照としてbytesとquotaを重複加算しない。再GateとActual規模をManifestへ渡し、TargetをPhysics品質へ流用しない |
| T-080 | Vehicle 1 cm Voxel Variant | Object境界とmeter基準1 cmのRecipeを再現する | Body／Exception Objectを別々に処理し、Transform／階層と親参照を保持する。D-155の2M以内の有効基底を正式保存し、200k超は用途Bindingなしで保持する。Triangleが減る／同じ／増える基底を省略せず、Presetの最大数以下の限定Post-Decimate、1回Ratio、NoOp／Alias、Resource分類を検証する。相対Voxel64／128／256へ読み替えない。ZCG後のBounds／中心／表面偏差とRenderCutInput Gateを再実行し、Licensed Solid体積／watertight／自己交差Gateを要求しない |
| T-081 | canonical成果物・用途・Synthetic | 10.2.2のSchema／Bundle／Artifact／Binding／sidecar／Report／Index／Receiptをboundedに検証する | 10.2.2のBuilding判定記録v1の一対一照合／canonical上限／判定一致Goldenと失敗境界とCatalog v2の全property／Rule×Eligibility×ScopeReason許可表、Rule Set hash、Batch Manifest v1の全一括割当／ordinal／7 queue／重複Source／外部hash／Report prefix、v2選抜schema、未知値／不正tag、成功出力参照必須・失敗出力禁止、Process／Selection分離、Recipe×Role許可表、NoOp／Alias先行参照、親hashとBlind由来制限、各Entryのprovenance、cohort、Catalog／frontier被覆、Bundle drift、file許可リスト、Receipt欠落を検査する。Slabの同一Source／Geometry、4枚以上、Transform／OBB／外周連続順、SlabBox逆参照を検証する。ZCG Golden／Numeric Kernel、単一Hull4／16／64／128 Cook入力、129 Role拒否、255 Codec成功、3／256／複数Hull拒否とFace／index導出上限を確認する。公開Triangle Positive8／Negative4、Cut期待値、4 Slab構造case、既存Synthetic Solid Validatorのepsilon／1 ULP／BVH／共有simplex検査を維持し、Licensedへ混在させない |
| T-082 | Capture Draft／Publication Recovery | 現行Record中心のライブCaptureをDraft中心へ置換し、最終Manifest確定前にもrequest、readback、PNG encodeを相関できる。freeze後はStaged Draftだけを原子的に最終Recordへ昇格し、Trace先行公開とCapture再試行を一意に復元できる | Factory／Registry／Submission／Scheduler／readback completionをDraftで通し、Drop Reason 0～9の固定値、既存1～4互換、各経路との一意対応、unknown Reject、lease予約失敗、Registry満杯、readback／encode失敗、PNG staging失敗、取消、freeze drain Timeoutのrollbackと`Pending -> Dropped`終端化を検査する。`受付停止 -> producer稼働中のbounded drain -> producer取消／join／静止 -> Terminal Intent Queue最終完全drain -> Queue／私有Buffer所有権照合 -> 残存Pending強制Drop -> 通常Trace producer静止 -> 通常FIFO完全Drain -> terminal列構築／専用Append -> Recorder Freeze -> Snapshot -> Summary`を各境界で停止させる。drain中とjoin直前の成功Stage／通常Drop Intentが最終drainで必ず処理され、完成済みPNGを理由9へ誤分類しないこと、drain中のEncoded／通常Drop EventとBarrier前残存Eventが通常領域だけへ入り、最大強制Drop＋RingFrozenが専用reserveへall-or-noneで入り、通常領域満杯でも`AwaitingFreezeTerminal`から早期Frozenしないことを確認する。terminal EventType／TestRunId／ID順／末尾Ring／件数、通常Queue非空時Append、直接APIの状態違反、PostRoll／reserve境界、通常領域overflow時Incomplete、reserve不足Profileを検査する。DroppedにPNGがなくてもFinalizerが成功し、StagedのPNG欠落、DroppedへのPNG混入、Pending残存、TestRunId／Context不一致、重複ID、件数不一致では最終Recordを1件も公開しない。Plan Schema v1のRunInitializationIdを含むproperty順／型／null禁止／最短integer／NFC、16 MiB／10万Entry／path／呼出側上限、非seek `limit + 1`を検査する。信頼base rootから`runs/run-{TestRunId}`を導出し、OS排他lockの同時取得拒否とprocess crash解放、staging作成直後／各init tmp・Rename／final作成／各ready確定でのcrashを再現する。片側root、空／tmp-only root、ready片側、完了後staging削除済みfinal-onlyを正しく復旧し、marker／InitializationId／Root hash／Peer hash不一致、同一／祖先base root、別Run再利用を隔離する。許可marker／tmp集合、rooted／UNC／drive／`.`／`..`／空segment／backslash／case-fold衝突／symlink／junction／reparse point／TOCTOU差し替えをRejectし、固定path導出とRun root内解決を要求する。staging file flush、Plan-last commit marker、Trace公開前durabilityを検査する。Trace公開前の各失敗点でFrozen入力とdurable stagingを保持し、Summary payload変更時はManifest hashではなくtrace／bundle index hashだけが変わることを確認する。Trace公開後はManifest hashを変えず、PNGだけ／sidecarだけ公開後のクラッシュから一致側を保持して欠落側だけを再試行し、内容衝突だけをhard errorとして上書きしない。`capture.index.tmp`書込中／flush後／rename前、Index確定直後／通知前／cleanup途中の各クラッシュを再現し、tmpを完了証拠にせず、Planと同一なら再利用、partialなら削除再生成、canonicalな所有不一致なら隔離する。全期待PNG／sidecar成功後に同じcanonical bytesの`capture.index`をPlan削除前にdurable確定し、CaptureCompleteと期待集合を復元する。Artifact検証は全長配列を作らない固定memory streaming length／hashを共通正本とし、Phase 0／0.1のdurabilityを維持する。Phase 0.11 Fresh専用条件ではfinal全hash 1回のPublish Receiptを同一CaptureCompleteへ再利用し、Recoveryはprocess-local証拠を捨てて再検証する。後日のArtifact削除／改変検出、pre-trace orphan隔離、明示放棄時のTraceOnlyCaptureIncomplete、bounded staging／verification buffer枯渇時のbackpressureも検査する |
| T-083 | 共用Geometry切断 | 共通契約を満たす入力を任意平面で切り、実Capを含む各非空出力が同じ閉鎖・edge／vertex manifold・局所winding整合を継承し、4.5.6の正負直接配置と転送・公開条件を満たしてから、表示とStencilへ同じ世代・Triangle集合としてCommitされる | 箱、凹形、複数の閉Component、全体反転、skinning後Self-intersection、別TopologyのCoincident／Nested Componentを切る。planeがvertex／edge／faceを通る場合、同一点複数port、極小／面積0 Triangleを含め、元surfaceと逆向きのCap boundary、Cap内部Edgeの2 incidence、単一vertex fan、canonical position、finite属性、再切断後の同契約を小さいFixtureで確認する。面積0 Triangleを含む非空GeometryとTriangle数0の空出力を区別し、後者へdummy Mesh／Cap／Rendererを作らない。受付後に一方のGeometryとConvexがともに確定空となるFixtureでは、確定前にLogicalCutOperationを公開せず、子1件／境界0件の確定結果をValidator後に原子的に公開する。用途別の二度目の切断／Cap生成／Uploadがなく、無効結果、世代不一致、出力予約不足は両用途とも公開せず最後の共用GeometryとPending Clipを維持することを確認する。出力予約不足だけは4.5.3の再予約・再実行を許容し、プール容量限界は4.5.4に従う。全Mesh self-intersection／inside-outside検査、旧救済経路、方式別試験を追加しない |
| T-084 | 共用Geometry入力Gate | 正常な閉Meshと全体反転を受理し、Boundary Edge、局所winding不整合、3面以上Edge、複数fan共有Vertexを切断可能Geometryとして登録しない。属性seamと別Topologyの同位置Componentを混同しない | 正向き箱、全体反転、複数の閉Component、Self-intersection、別TopologyのCoincident／Nested Component、UV／Normal seamを受理する。開放Boundary、1／3／4面Edge、T-junction、局所反転、複数fan共有Vertex、共有position不一致、NaN／Inf、不正index／Topology参照を入力準備時にRejectする。Runtime生成の面積0 TriangleをGeometry全体の空と誤判定せず、片側空No-opはこの入力Gateではなく7.6の現在Convex分類で判定する。全Mesh自己交差、inside／outside、signed volume、向き正規化を実行せず、同じ不合格行列をStencil描画試験へ重複させない |
| T-085 | Convex質量特性と質量保存 | 重複Compound Convex、非交差Convex、複数Convexを横断する切断、連続切断でも共用Cut Geometry／Union Solidを積分せず、親質量を保存した子の質量・重心・慣性を決定論的に生成できる | 単一箱、重ならないCompound、部分／完全重複Compound、凹形状を覆う複数Convex、専用Physics Proxyを持たない共用Geometryを切断する。LogicalConvexFragmentLocalId順のbinary64左畳み、並列Reduction／再関連付け禁止、各中間finite、親質量正、Weight 0許容、全Weight 0／非finite／overflowを検査する。片側の子がWeight 0 Convexだけを持つFixtureでは、空集合とみなさずGeometryを保持し、独立物理が成立しないため現有効な単一FragmentGroupまたはProvisional Actor集合を正式採用して`Stable Unsplit`へ終端する。質量0／任意最小質量Rigidbody、部分的な物理Commit、Siblingへの質量移送、実装固有の質量再配分を禁止する。`assignedMass = parentMass * (weight / weightSum)`、`densityScale = assignedMass / convexVolume`、`I_assigned = I_unitDensity * densityScale`の固定順をGolden値へ照合する。切断前後の質量和、子孫世代の累積誤差、重心、慣性主値、平行軸合成、列挙順不変性、生体積加算による重複質量なし、非交差Weight不変、交差Weightだけの体積比分配を確認する。片側体積がepsVolume以下でもGeometryを消去せず、正当な配分が成立しなければ現物理を正式採用する。体積0／非有限／極薄Convexでは正確積分、Convex OBB、Fragment OBB／AABB、現物理の正式採用の順とTraceを検査する。`MassProperties`のSingle-Thread時間、Job Throughput、一時メモリをT-076系列で測定する |
| T-086 | 非Union Geometryと物理所属 | 複数の閉ComponentをUnionせず正負二集合へまとめ、7.6の空／非空規則で所属する | U字や離れた島を含む少数例で、同SideのGeometry／Convexが一つの所有者のまま正常Commit・再切断できることを確認する。Convex片側正常空は1物体、両側非空でGeometry片側空はRendererなしを含む2物体とする。cook・Weight失敗を空にせず、必要なら既存物理を正式採用する。正負が同じ所有者へ付いても面・Side・Capを保持し、専用Convexや所属探索を作らない。同じ描画条件のCapは非Unionの符号加算とし、Overlap Siblingの一般衝突抑止は維持する |

4.5.6の表示出力は既存T-007／T-083／T-086／T-090／T-091の該当確認へ次を統合し、小さいFixtureと意図的な完了遅延で確認する。別Test ID、専用大規模Suite、Benchmark Schema、Trace Eventを追加しない。

| 確認する順序・条件 | 既存試験へ統合する期待動作 |
| --- | --- |
| 正負直接出力（T-083／T-086） | 一つの予約へCapを含む正側、負側の順で連続配置し、n0・n1から範囲を決める。未使用tailを公開・転送せず、面集合・向き・Topology・再切断結果を維持する |
| Index転送（T-007／T-083） | 新規出力の実使用範囲を一度のSetDataで転送する。入力Geometry全体と既存GPU範囲を変更せず再利用する場合だけ新規Index書込み・転送0回とし、片側出力や個別非交差Triangleだけでは省略しない |
| 表示CPU結果が先着（T-083／T-090） | Final cook・物理所属の確定を待たず転送でき、現在有効なframe／参照で表示追従できれば先行Commitする。後着物理への参照切替で再配置・再転送しない |
| 物理が先着（T-086／T-091） | 必要なCPU情報と適用条件が揃えば表示転送を待たず物理Commitし、仮表示は現在物理に追従する |
| 既存物理の正式採用・失敗（T-086／T-091） | Stable Unsplitでも正負Index配置を維持して現在物理へ表示を所属させる。Frozen・失効は既存の安全・回収規則へ進み、来ないFinal成功を待たない |
| 再切断と寿命（T-007／T-083／T-091） | Operation公開後はGPU未転送CPU入力から再切断できる。Published入力を上書きせず、必要な未転送継承Vertexを供給し、旧範囲は全CPU／GPU読者終了まで保持する。Geometry Commit後も必要な物理工程が終わるまで未完了件数を維持する |
| 先行計算（T-007／T-083） | 有効な準備済みGPU範囲を再利用し、命中後の再配置・再転送を要求しない。前提不一致は既存の不採用・回収に従う |

T-082では追加で、`MaxInFlightDraftCount`が受付済み全Pending Draftをqueue横断で厳密に制限し、Registry外Pendingが存在しないことを検査する。freeze時のimmutableな`ForcedDropFrameIdSet`に対して、terminal列の欠落、余分、重複、順序違反、Reason違反、TestRunId違反、Ring欠落／複数を個別に与え、すべてall-or-noneでRejectされcapture列が不変かつ`AwaitingFreezeTerminal`に留まることを確認する。Buffer構築失敗、検証失敗、reserve書込み失敗の各地点から同じ集合で再試行して初回成功時だけFrozenとなり、成功前はSnapshot／Summary／Manifest／Plan／Exportが不可能で、明示Abortではbundleが公開されないことを確認する。Run root所有権はstaging／finalの2 lock pathを決定順に取得するものとし、異なるstaging base＋同じfinal base、同じstaging base＋異なるfinal base、2本目取得失敗、逆順要求、process crashを試験する。途中失敗では先に得たhandleが解放され、両Run rootが未変更であり、再試行可能であることを確認する。

T-082ではさらに、通常領域に空きがある状態と満杯の状態の双方で`BeginFreezeTerminalAppend`だけが`CapturingPostRoll -> AwaitingFreezeTerminal`を起こし、producer稼働中、通常Queue非空、drain未照合、およびBegin再呼出しをRejectして状態とcapture列を変えないことを検査する。terminal reserve有効時のpublic `Freeze()`が直接Frozenへ進めないこと、Legacy reserve 0だけが旧契約を維持することも固定テストに含める。`MaxInFlightDraftCount`と`MaxDraftCountPerRun`の境界、終端Entryを保持したままPending Slotを再利用する長時間Run、総Entry 100,000件、100,001件目の受付拒否を試験する。Pending不足／総Entry不足の`CaptureFrameAdmissionRejected`はID 0とKind／Value1の固定割当を持ち、Draft／Dropped／Plan件数へ入らないこと、理由5を`RecordDropped`へ渡すとRejectされることを確認する。

T-082ではLogger Seal境界をproducer enter前／active中／退出直後／Sealing後／最終Drain後へ移動し、active writer数のincrement後に行う`Open`再確認の成功をenqueue成功の線形化点とする。この線形化点が`Open -> Sealing` CASより前のEventだけがQueueと通常領域へ入り、active writer数のincrementがCASより前でも`Open`再確認がCASより後ならQueueへ入らず、Sealing中の拒否としてcutoff前ならRun Failure Count、cutoff後／Sealed後ならPost-Seal診断Countだけを増やすことを検査する。raw ParallelWriterをCapture Runから取得できないこと、Main Thread EnqueueとBurst writerが同じgateを通ること、late enqueueがあってもBegin／Appendが停止しないことを確認する。forced drop 0件／1件／上限件で各terminal Eventの22 fieldを1 fieldずつ改変し、Draft Trace Context、Checkpoint、未使用0、状態、Reason、Value、負の0／非有限の不一致がall-or-noneでRejectされることを試験する。既存2引数constructorがreserve 0、Capture Factoryがchecked `MaxInFlightDraftCount + 1`を設定すること、internal constructorの負値／超過／overflow、reserve有効時public `Freeze()`のfalse・無変更、Legacy時の既存bool挙動を固定テストへ含める。

T-082ではFailure Count cutoff直前／同時／直後にSealable writerを競合させ、各拒否がSealed Run CountまたはPost-Seal診断Countの厳密に一方へ入り、Sealed Countと生成済みSummaryが以後変化しないことを検査する。Summary取得後に保持済みwriter copyから試行してもQueue、Sealed Count、bundleのStateが変わらず、Post-Seal Countだけが増えることを確認する。通常Draft Dropの理由6～8と強制Dropの理由9へ同じ非ゼロSlashId、FrontEdgeId、ObjectId、ObjectGeneration、TaskIdを持つ既存CaptureFrameTraceContextを与え、いずれも12相関fieldが一致し、`FromState=Pending(0)`、`ToState=Dropped(2)`、元ContextにないSlashGeneration／Mob／Planだけが0であることを全22 field Validatorで確認する。通常Draft Dropについては、単一Terminal CoordinatorへDrop対Drop、Drop対Stageを同時投入し、確定した先頭Intentだけが共有資源のrollbackまたはStaging採用、Registry終端遷移、Pending Slot解放を各1回実行し、敗者が勝者の資源へ触れないことを検査する。Dropped確定直後にLogger破棄、seal競合、Queue／Native書込み失敗を個別に注入し、Trace enqueueが失敗してもDraftがDroppedのまま、freeze時のForcedDropFrameIdSetへ入らず理由9へ再分類されないことを確認する。失敗した通常Drop Traceはcutoff前のRun Failure Countを増やしてRunをIncompleteにし、RegistryのDrop Trace発行状態は成功・失敗とも`Attempted`へ一度だけ進む。同じCaptureFrameIdで消費APIを再呼出ししてもEvent、Failure Count、Draft状態が増減しないことを検査する。Legacy `RecordDropped`の理由1～4は既存の`FromState=0`／`ToState=0`を維持し、新設`RecordDraftDropped`が理由6～8だけを受理すること、理由9を両通常APIへ渡すとRejectされterminal Builderだけが生成できることも固定テストへ含める。既存CaptureFrameProfileの7引数constructorと2引数`CreatePhaseZeroUnityLeftEye`の結果が不変でTrace容量を持たず、PhaseZeroCaptureProfileSetが4096／32／10000を返し、Profile ID不一致、Trace Profile境界、Factory構築を決定論的にReject／受理することを試験する。

T-082ではTerminal Intent Queue容量を`checked(2 * MaxInFlightDraftCount)`の直前／一致／1件超過で検査し、同一Draftの未処理Intent上限2件と同一DraftについてRun中に受理される総数上限2件、3件目の拒否、Queue全体満杯、Coordinator drainとの競合を試験する。`TerminalIntentEnqueueStatus`の全固定値について、`Accepted`だけが私有Buffer所有権をCoordinatorへ移し、`Backpressured`だけがproducer所有のまま再試行可能、`DraftAlreadyTerminal`／`IntentLimitExceeded`／`RunNotAccepting`はproducerが私有Bufferを解放して再試行しないこと、`InvalidIntent`は所有権を移さずRunをFail Fastすることを検査する。Queue満杯後はdrainでAcceptedへ進む一方、受理総数2件到達後の3件目は何度待ってもBackpressuredへ変化せず、無限再試行しないことを固定する。複数条件が同時成立する場合のstatus優先順も1件ずつ試験する。freeze取消時はBackpressured Intentを再試行して受理させるか、`RunNotAccepting`を受けてproducer自身が私有Bufferを解放し所有数0をacknowledgeするまでjoin成功とみなさない。join直前の最後のenqueue、join後Queue非空、最終drain途中を個別に停止し、最終drain後だけQueue件数0、受理数と処理数一致、Queue所有Buffer数0、producer保持Buffer数0となること、その後の残存Pendingだけが理由9になることを確認する。

| ID | 対象 | 合格の考え方 | 方法 |
| --- | --- | --- | --- |
| T-088 | Player非接触Locomotion | Player Body／Handが切断前後のプロップへImpulseを与えず、モデル化済みの簡易Occupancyだけで人工移動の代表的な新規壁内侵入を抑え、Camera被りを許容しながら刀／斬撃波Interactionと未来物理の再利用性を維持する | Player／Prop Layer接触を監視し、静止壁、Episode開始地点から2 m超を通常移動した後にPlayerへ侵入する移動Slab、対向する2 Volume、角での3個以上のOverlap、中心一致、回転Primitiveの垂直法線、周期振動を誘発する交互最深Volume、Pending旧Collider、切断開口、HMDの実空間Leanを試す。各Fixed StepのExitSearchBoundsが現在Capsuleと最大候補Sweepのunionへ再配置され、遠方Slabを漏らさず、同一Tickの全候補でBounds／Volume集合が不変であることを確認する。最大候補Sweepが`ExitSearchMaxHorizontalExpansion`ちょうどなら評価でき、1 ULPまたは規定Fixtureで超えた場合はVolume Query、部分候補、Player移動なしで`SearchBoundsExceeded`となることも検査する。移動Slab起因では物理Commitを巻き戻さず`ForcedOccupancyOverlap`へ入り、AllowedLocomotionPlane上の有限候補を全関連Volumeで評価すること、適用ごとに`(MaxDepth, SumDepth, DepthByVolumeId[])`が厳密減少して同一Snapshot内の周期を作らないこと、epsilon未満の入力にも最小量を要求しないことを確認する。Volume数と展開候補数は各上限ちょうどで固定長領域内に収まり、1件超過、checked count overflow、候補容量超過では部分Volume／部分候補を評価せず、Playerを1 Tickも動かさず対応Reasonの`OccupancyExitBlocked`へ入ること、Episode中にManaged allocation／Buffer成長がないことを検査する。有効な平面内減少方向がないFixtureは振動や任意軸移動をせずBlockedへ移り、毎Tick厳密減少する極小進捗Fixtureも`MaxForcedOverlapFixedSteps`で`EpisodeTimeout`になることを確認する。Fade、明示的な安全Pose復帰、Occupancy移動後の再開、全深度がexit epsilon以下での通常Policy復帰、HMD非Clamp、Player接触Impulseによる物理予測Reject 0、SlashFront命中と切断Commitも検査する。さらに未登録の小型／装飾／非干渉物体がHMDへ被ってもCameraや物体を強制移動せず、厳密Mesh包含検査を起動しないこと、即時Stencil処理中またはNear Plane交差で部分Cap、Cap欠落、内部面、左右眼差が生じても切断／物理／Geometry Commitを失敗させずJob再発行や同期Fallbackを行わないこと、Stable Geometry置換後にTemporary Stencil由来の部分Capを残さないことを確認する |
| T-089 | Hybrid Clip Plane予算 | D3D11／Quest LinkのColor、Depth、Shadow、Stencil Volumeで同一のstable Plane選択を使い、Raster 8面とPixel fallback最大4面でGPU時間とMSAA edge品質を保ちながら、容量超過面をRendererだけから無視できる | 0、1、7、8、9、12、13、32候補面を持つ単一／複数RenderFragmentを用意し、先頭8面が`SV_ClipDistance`、9～12面がPS `clip()`、残りがIgnoredになることをShader captureとProfiler Counterで確認する。Operation公開前後で受付済みの未Commit面・Sideを引き継ぎ、固定による候補除外がないこと、RenderFragment対応変更時に同じ描画更新境界で候補を再構築することを確認する。Pending Cut列とCutBoundary公開列を通した受付の古い順、左右眼、Color／Depth／Shadow／Stencil各Pass、カメラ移動、画面外復帰で選択が一致し点滅しないこと、Operation公開時に同じ面と受付位置を保って重複しないことを検査する。同一枝へ13回以上連続切断し、選択列が未Commit祖先についてdependency-closedで、Ignoredな後発面により祖先外Geometry復活やSibling重複を生じないことを確認する。順序違反した復元Fixtureは違反以降をIgnoredとして背景完成へ委ねる。Ignored Pending Cut／境界でもPending CutまたはCutBoundaryRecord、世代、支持、背景共用Geometry／Convex Jobが残り、同期待機やJob cancel／再発行を生じずStable Commitで正しい形状へ収束することを確認する。Ignored VolumeのCap板をBatchへ残し、通常Colorで別ResidualがないsampleはStencil 128のためColor／Depthを書かないこと、通常Colorの非互換Residualは分離されること、最後の統合Colorでは板の可視化と誤Depthを5.2の例外として扱うことを確認する。Ignored専用フラグ、compaction、代替VFXを要求しない。MSAA 1x／2x／4x／8x、pixel-bound／vertex-bound Sceneで全PS clip、Hybrid、Raster 8のみを比較し、Pixel fallback数とStable専用Shader分離をO-043へ記録する |
| T-090 | 最小優先度Dispatcher | 固定容量・非割当のV1が物理安全をBackgroundより先にScheduleし、低優先度投入でCritical予約枠を消費せず、Schedule前取消とSchedule済みGeneration Rejectを一意に処理する | 5 PriorityClass、同一Deadline、Deadlineなし、同一Class stable順、Queue上限一致／1件超過、Critical予約枠、Tick Schedule／費用予算、Background starvation、Batch化、取消競合、二重Completion、古いGenerationを合成Work Itemで再生する。通常のDispatcher APIはCapacityExceededで待機・eviction・同一Frame無限再試行・Managed allocationを生じないことを確認する。全対象合計の`MaxIncompleteCutOperationCount`直前／一致、同一Frameの複数対象、同一Slashの部分受付を試し、見送りが状態・世代・新規仕事を変えず再実行されないこと、受付時と正常完了／世代失効後回収時にだけ件数が一度増減し、CPU Readyの先着でも、現在有効なframeで先行Geometry Commitして物理処理を残す場合でも件数が減らないことを確認する。Geometry先行Commitは4.5.6の現在frame・参照・必要転送条件で確認する。受付済み切断の必須仕事だけは切断Coordinatorが既存Schedule／Completion／Result適用を同期進行して1回再投入でき、進捗不能または次のPhysics Stepが必要なら既存Pendingへ持ち越すこと、PlayerLoop／Commitへの再入、全切断待機、無限再試行を起こさないことを検査する。全`EnqueueOutcome`、無効Deadline、Sequence枯渇、受付失敗時のInvalid Tokenも境界試験へ含める。V2相当の高度機能を実装せず、受付結果はOutcome／Counter、受付成功後のSchedule／Completion／Cancel／Generation Rejectは既存Traceから復元し、存在しないEnqueue Eventを要求しない |
| T-091 | Provisional Rigidbody／Collision Proxy | cook待ち中も各既知物理子が連続したpose／速度と外界Collisionを持ち、再cookなしの旧Convex再利用から物理Actor優先でFinal Colliderへ移行する | 単一Convex、非交差Compound、切断面を横断するCompound、Anchored／Detached、両側Anchored、1子／2子、同Sideに複数の離れた島がある出力、cook待ち中の連続切断、外部Dynamic物体、床接触、Ghost Contact、小物の切断隙間侵入を試す。非交差Shapeが片側だけ、交差／epsilon内Shapeが両側へ割り当てられ、同系譜SiblingだけCollision無効、外界Collisionは全有効であることを確認する。Provisional生成でcook 0、Geometry Resource共有、固定容量内のActor／Shape／Constraint原子的公開を検査する。点配分未完了は既存Work依存とし、固定側を含む対象の一時的な実行枠不足では`PendingAnchoredSplit`として既存物理を維持し、判定完了または枠の解放後に通常処理へ進むことを確認する。点Anchor配分が完了し全所有者がAnchorなしの対象の一時的な実行枠不足は`PendingPhysicsSplit`とする。要求Actor／Shape／Constraint数が固定上限ちょうどなら成功し、1件超過、Backend共有不可、または既存の安全規則上の資源確保・原子的構築不能では部分公開せず、現物理を正式採用した`Stable Unsplit`へ終端できることを確認する。Provisional生成不能だけで有効な後続Final物理処理を打ち切らない。OBBの正負体積比と補数によるSide Budget、全0／非finite時の等Weight、連続切断で各世代のProvisional質量和が親Canonical Mass Budgetと一致すること、正質量を作れない場合は部分公開しないことを確認する。初回Provisional生成ではRender Anchor pose／点速度／角速度が連続する一方、Final handoffではActor pose／COM線速度／角速度がbitwiseまたは規定epsilon内で不変で、Final Shape全頂点が由来Provisional Convex half-spaceの`FinalContainmentEpsilon`内に収まることを確認する。包含不能、張り出し、frame不一致ではFinal Shapeを公開せず利用可能な既存物理を正式採用して終端し、Colliderを動かして外界penetrationを作らないこと、表示だけの瞬間移動を許容すること、Final分離Impulseを二重適用しないことも検査する。D6／Custom Constraintの再侵入、Solver時間、最大接触Impulse、Broadphase pair、生成／破棄時間、Sleep率を測り、公開後の非finite／異常速度／Constraint失敗だけが安全封じ込めとなること、Timeoutでも同期cookやpose巻戻しを行わないことを検査する |
| T-092 | Mob固定ステップ軌道Cache | 同じSnapshot、Intent、Path、Seed、PlanGeneration、Animation Clip Catalogから同じRoot軌道と`ExplicitAnimationStateV1`列を生成し、Nearのライブ更新とMid／FarのQueue再生が同じ移動Kernel／Animation Plannerを共有し、Jobベイク採用時は未来Pose／VP入力準備へ接続できる | 固定MobId順、Current／Next二相更新、FixedStep倍率、Waypoint／Lane、Queue wrap、Horizon補充、Render補間、移動距離由来PhaseとPlaybackRateCyclesPerSecondを再生し、V1ではMirror入力もBackend固有Mirrorも生成しない。Render補間では`HorizonSampleCount = 1`、`2`、開始直前、開始ちょうど、終端ちょうど、終端超過、最後のSample直前1 FixedStepでoff-by-oneやHold条件の逆転がなく、`stepId < StartFixedStepId`では先頭Sample全体でHoldし、`stepId < CommittedThroughFixedStepId`のときだけ同一Clip Stateを補間することを確認する。Group公開は全Mob descriptor検証後の単一Group epoch atomic storeだけが読取可能点で、Commit途中の一部Mobだけ新HorizonまたはClip／Phase／Rateになる観測がないこと、旧Job完了、入力末尾Sample slotのpin／Snapshot、wrap時の未再生上書き禁止、Reader完了境界後の旧slot回収、epochのwrap／ABA対策を検査する。`HorizonSampleCount * CrowdStepScale`、`StartFixedStepId`加算、`stepId`減算のchecked overflowではPlan／補充を公開せず既存区間維持のHoldとなり、`FixedStepId`のwrap／再利用で古いSampleが未来区間として再利用されないことを確認する。NavMeshAgent／Root MotionがRoot位置を二重更新しないこと、全Plan／Group無効化でGenerationが進み旧軌道・未来姿勢と、Jobベイク導入時のVP入力準備結果が採用されないことを確認する。人形の切断成果物の実Commit拒否はJobベイク採用時のPhase 5.1で統合確認する。固定容量の最大Mob／Sample数、Background Queue満杯、Mid／Farのunderflow、Near Live Fallback予算超過では再確保・Main Thread待機・無制限再試行を起こさず、規定のState全体Holdと固定Profiler Counterへ低下し、既存MobPlan lifecycle Traceが矛盾しないことを確認する。ORCA、依存Graph、Flow Fieldを無効のままでもPlayableで、多少のMob重なりを許容してCPU、Nativeメモリ、Queue枯渇率、計画再利用率を測定し、Jobベイク採用時だけ候補からの未来Pose／VP準備要求・結果取得・失効を確認する。先行切断完了率・Commit率はその後のPhase 5.1で測定し、不採用時は両確認を要求しない |
| T-093 | 剛体切断Local Planeリベース | 実姿勢を維持し、面採否と切断受付判定をGeometry Job完成から独立させ、受付済みの全表示・物理処理が同じ操作面へ収束する | 19.5.1の対象、接線／法線並進、回転、重心と原点不一致、左右眼差、閾値直前／一致／超過、Near Plane／非finite／Profile欠落、Scope外を小さい固定Fixtureで確認する。同じDescriptor／実SnapshotでMesh完成済み・遅延中・Job失敗を切り替えても採用面と7.6の片側空判定が同一で、Ready前にNo-opならCutOperationId／Pending Cut／操作／世代／表示／物理／仕事を作らず、受付済みならCutOperationIdとPending CutがLogicalCutOperationより先に公開され、Operation未公開のTemporary表示と後のOperation／Provisionalが同じ採用面を使うことを確認する。Operation公開時はPending Cut由来の描画位置と面を維持して重複Recordを作らず、同面Job継続／後追い、後着不採用Job回収、世代更新前Snapshotと受付後世代の対応、再切断Rejectを検査する。FinalまでActorが動いても巻戻しなし、面変更なし、由来Convex包含、実速度継承、Impulse一回をT-091の代表caseで確認する。SourceSlashPlane／有限Sweep不変、別対象の面差とCap Group分離、LogicalCutOperation作成Traceに先行できる3 Event束によるPending Cut面の復元と欠落／重複／混在拒否、後着Operation Traceとの相関を検査する。少数の動画像で近似誤差とVFX差を目視し、既存Captureを使用する。全距離・視野角・閾値の直積、全Contour最大誤差証明、新しい長時間性能SLAを要求しない |
| T-094 | Building World D6 lifecycle | 7.2.2のMetadata、物理公開時のDepth、対象子へのWorld D6生成とActor寿命を確認する | T-059／T-061／T-072／T-091の既存公開・handoffを使う少数Syntheticで、建物true／非建物false・初期Depth 0、建物由来の通常1→2と連続分裂でのDepth更新、非建物分裂後のfalse／0維持、片側／両側Anchorあり、式どおりの有限・非負なLimit設定、生成時相対並進・回転0、World垂直Free／水平・全回転Limitedを確認する。整数上限やunderflowの境界試験はPhase完了条件に含めない。1物体結果・No-op・Stable Unsplitで変更せず、Provisional 2 Actor公開後のFinal片側空・既存Provisional正式採用でもDepthを巻き戻さず、Final handoff／UpgradeでD6を再生成・基準リセットしないことを確認する。D6構築不能は既存Pending／構築不能規則を使い、部分公開しない。Actorと同じ既存寿命後に所有D6を一度だけ回収し、既知Constraintを予測入口から除外できる識別境界を確認する。一般外部Jointの探索・付け替え・保護・複製経路は要求しない。実際の倒壊防止・変位／回転上限・jitter・penetration・Solver収束の合格閾値は設けず、Joint数とPhysics Step費用を記録できる。製品Recipe、任意分割・GC、未来予測本体は各後続Phaseで確認する |

未来用Jobベイクの確認は少数のSkinned Fixtureと既存の共用Geometry検証を使う。Phase 4.65は固定Rig Poseの限定実装で、4.5.2の条件を揃えたBakeMeshとの必要属性・頂点／Topology対応、非同期回収、21.2の負荷比較を行い、導入の採否を決定する。対応入力の少数比較にRendererとRoot Boneのframeが異なる例および非単位scaleを含め、入力をWorldへ写した形状も比較してscaleの二重適用・適用漏れがないことを確認する。効果がなければ不採用で完了し、本体DAG／VPプール接続、MobPlan、実命中、Ragdoll、人形切断の完成を要求しない。Phase 5の基本切断はT-008で確認し、T-018のPose／前提検証とT-092の計画単体確認を後段統合待ちにしない。

Jobベイクを採用した場合のPhase 5.1では、T-008／T-018／T-092の人形先行切断への接続として、準備済み結果の採用、未完成・Pose不一致時の現在Pose同期経路、世代失効後の後着結果回収を少数ケースで確認する。既存Geometry／Physics Commitへ接続し、4.5.6の直接Index出力・独立転送・現在frameでの公開条件と、実Actor／Animator内部Stateを予測へ巻き戻さない規則を維持する。21.2の同時候補負荷と先行完成・採用状況を測定する。新しい試験ID、専用Framework、全Asset・全変形機能の網羅試験は要求しない。

T-093の「再切断Reject」は、Operation公開後に新世代の切断を受付けた結果として旧成果物をRejectする既存の世代競合を指す。Operation公開前の後続切断の見送りと非再生はT-007で検査する。

T-005の削減成功例を再切断とT-091のcook／handoffへ接続し、採用した削減済みB-repが次回入力となり、既存のFinal包含・frame・親質量保存を満たすことを確認する。縮小由来の接触消失・運動変化は7.2の品質許容とし、Actorの巻戻しや不正形状の公開を成功扱いしない。削減専用の試験ID・Fixture体系・cook結果抽出基盤は追加しない。

T-091では、4.5.6の直接配置・必要転送・現在の表示追従条件を満たして共用Geometryを先行Commitし、Temporary描画対象を回収した後も、対応するPending Cutの受付情報が残り、世代、採用面、対象、所有権および`CutOperationId`が一致する有効な後着Physics成果物を通常どおりCommitできることを確認する。Operation未公開の終端失敗後の後着成果物RejectはT-007のまま維持する。

T-074の点Anchor確認をT-091のProvisionalへ接続する。同じ旧Cooked Geometryを共有しても分類側だけがAnchorを継承し、再切断で祖先Bufferから復活せず、Final Shape交換と同形状再cookで位置・所属が変わらないことを確認する。少数の既存Fixtureを使い、新しい試験体系は追加しない。

T-090では入力`EvaluationWorkItem`にSequence fieldが存在しないこと、Descriptor不正／CapacityExceeded／NotAcceptingが次の成功受付のSequenceを進めないこと、受付成功だけが連続Sequenceを内部Recordへ割り当てることを検査する。不透明`WorkToken`からSequenceのbit layoutを推測せず、`TryGetState`の診断SnapshotとTask lifecycle Trace `Value1`が同じ内部値を返すこと、古いToken世代が再利用SlotのSequenceへアクセスできないことも確認する。

T-091では`ProvisionalPhysicsCommitted`、`ProvisionalPhysicsFallbackActivated`、`ProvisionalPhysicsFinalized`、`ProvisionalPhysicsSafetyFrozen`の成功enqueueが各ゲーム結果につき1件、Trace enqueue失敗時は0件＋Run Incompleteで、状態rollback、Trace再試行、重複Eventがないことを検査する。全`FragmentGroupPhysicsState`固定値、公開前Fallback Reason 1～10、公開後Primary Fault Reason 1～4とTraceReasonの一対一対応、Containment Disposition 1～3、CutOperationId、ObjectGenerationをEventから復元し、Unknown state／Reason／Disposition、Generation不一致、同一結果二重消費を原子的にRejectする。公開前Fallback EventはFromStateとToStateが同じ実状態で、`Value1`だけが要求したProvisional状態になることを全開始状態で確認し、存在しないProvisional遷移をTimelineへ作らない。Resource LeaseはActor公開前の全件取得、構築失敗時の逆順rollback、Final交換／連続切断／Generation Reject／Timeout後のShape除去と物理ステップ完了、最後の参照後のGeometry破棄を試し、use-after-free、二重返却、Lease leakがないことを確認する。

T-091の公開後Fault試験では、2個以上のProvisional Actorへ非finite pose／速度、線速度上限一致と1 ULP超過、角速度上限一致と1 ULP超過、Constraint破断を個別および同時に注入する。Primary Faultが`NonFinite > Constraint > LinearVelocity > AngularVelocity`、同順位は最小LogicalFragmentLocalIdで一意に決まり、原因ReasonをContainment結果で上書きしないことを確認する。正常Fixed Stepごとに非公開Slotへ全Actorを収集し、同一FixedStepId、同一世代、ActorCount、LogicalFragmentLocalId順、finite性を全検証した後だけ公開Slotが切り替わることを確認する。Actor 1件目更新後、途中Actor更新中、検証後atomic切替直前にFault／世代変更／Actor集合変更を注入し、Stagingが破棄されて旧完全Group Snapshotだけが復元に使われ、現Step値や異なるFixed StepのSibling poseが混在しないことを検査する。Fault時はGroup全体だけが`ProvisionalFaultFrozen`へ一度遷移し、旧完全Snapshot復元はDisposition 1、Snapshot不在のScene除外は2、封じ込め事前検証失敗のScene除外は3となる。全Constraint解除、全Actor Kinematic、速度／角速度0、Force／Torque消去を検査し、Snapshot欠落、世代不一致、封じ込め事前検証失敗では部分FreezeせずGroup全Actor／ShapeをSceneから除外する。Fault後にFinal cook完了、後続切断、Trace enqueue失敗を発生させてもDynamic復帰、Final物理Commit、旧Group rollback、二重SafetyFrozen Event、Lease早期返却がなく、共用Geometry背景Commitだけが継続できることを検査する。Snapshot値を表示補間や表示―物理誤差収束へ使用する実装は不合格とする。

T-081では、10.2.2の新規canonical schemaを生成前にGolden固定し、旧v1選抜入力をv2として暗黙受理しない。ProcessStatusは予定Entryだけ、Geometry／sidecarのSelectionClassは成功物だけに持たせる。StructuralSlabExtractのtag違反、NoOp／Aliasへの新規Geometry、未解決親、TriangleMeshのPhysicsCookInput、129頂点HullのPhysicsCookInput、Blind由来のSelected化を拒否する。Synthetic Validatorの数値／byte契約は変更しない。

T-081ではEarlyFixtureBatchAllocationProfile v1／EarlyFixtureResourceProfile v1／Catalog v2／EarlyFixtureBatchManifest v1の上記canonical契約をGolden固定する。Catalog先頭／途中のExcludedが投入数・順を変えないこと、CatalogEntryOrdinalとBatchOrdinal／BatchSourceOrdinalを混同しないこと、必要stratum不足時に全queueを消費せず終了することを検証する。一括Manifest作成の中断／再開、欠落・余分・改変・Source重複、割当済み未実行の維持、最終Reportのfrontier内完全被覆／frontier外非混入を確認する。新Profileによる再評価でも生成provenanceと割当Profileを変更せず、既存Artifactを検証再利用できることを確認する。Catalog改訂は新identityとし旧Receiptを変更しない。CatalogのSourceBundleContentSha256欠落／不一致、参照fileだけ同じで余分なSourceを含む別Bundleへの差し替え、実treeへの余分file追加、CatalogをSource Index内へ含める循環配置を拒否する。Receiptから正確なBundleまで検証できることを小さいfile-tree Fixtureで確認する。Triangle帯の境界、quotaのSource重複加算禁止、観測値のDataset hash非影響も維持する。 CatalogのSourceTriangleCount禁止、64／65文字ID境界、BatchAllocationProfileContentSha256への名称統一と旧field拒否を検証する。

## 15. 実装ロードマップ

| 段階 | 焦点 | 主要成果物 | 完了条件 |
| --- | --- | --- | --- |
| Phase 0 | 非VR基盤・観測 | Unity 6.3 LTS 6000.3.22f1、Universal 3D／URP、Repo・ignore・Package Lock、固定テスト、Editor更新手順、入力抽象化、WorldPhysicsProfile、ProfilerMarker、Flow、TraceLogger、最小タイムライン、FrameId同期のUnity選択的キャプチャ、CaptureFrameDraft／CaptureDraftRunContext／Factory／Registry、Draft状態／Drop tombstone、append-only Drop Reason、Freeze Barrier／通常領域とterminal専用reserve／AwaitingFreezeTerminal、Draft対応Submission／Scheduler／readback completion、OS lock／二相Run root marker、Run専用Durable PNG Staging Store、CaptureFrameDraftFinalizer、canonical CapturePublicationPlan／path-safe bounded Loader、永続Capture Index／tmp Recovery、FrozenRunPublicationCoordinator、Summary付きExport Snapshot、Trace／Capture二段階公開と再試行Recovery、T-019／T-020／T-082 | 固定Editor版から非VRで再現可能な性能基準、重力Profile、Work Item／Job時系列、対応画像を取得する。ライブCaptureは最終Manifestを要求せずDraftとPNG stagingまで進む。受付停止後にin-flightをdrainしてproducerを静止し、通常FIFOを通常領域へ完全Drainしてから、強制Drop／RingFrozenだけを専用reserveへ直接追記してRecorderをFrozenにする。freeze時にPendingを残さずterminal TraceをFrozen列へ含める。Stagedだけを既存CaptureFrameRecordへ原子的に昇格し、Droppedは期待集合から除く。TestRunIdでRun rootを導出し、OS lockと相互binding markerで排他的に初期化／Recoveryして、PlanとstagingをTraceより先にdurable確定する。Trace bundle公開前失敗では同じFrozen Runを再構築し、公開後の一部Artifact失敗では最終Manifestを変えず、片側公開も含め欠落fileだけ再試行する。全期待CaptureFrameIdのPNG／sidecar検証後に永続`capture.index`を確定して初めてCaptureCompleteとなり、一時worktreeで更新・復帰手順も確認する |
| Phase 0.1 | Capture非同期化 | Phase 0で完成したPNG＋JSON形式と`PngJsonCaptureEvidenceBackend`を維持し、固定Unity版でthread-safeと規定された現行`ImageConversion.EncodeNativeArrayToPNG`をWorkerから使う単一路線とする。PNG encode、canonical JSON、hash、durable stagingを固定容量Workerへ移し、Main Thread PNG Fallback、実行時Capability分岐、別PNG libraryを実装しない。Main Thread上の`TryCollect*`は固定容量Completion経路の軽量pollと正式状態遷移への反映だけを行い、final publication、Recovery、CaptureComplete、cleanupは既存Coordinatorの責務を維持する | Tier A通常CIの数Frame固定FixtureだけでWorker受付、Completion Queue、Drain／Join、Worker例外、Main Thread Fallbackなし、Backpressure／Drop、FrameId／Trace相関、既存Loader／Verifier互換を検査する。Worker出力がlosslessに同じRGBA pixel、寸法、orientationを復元しcanonical JSONを維持することを確認するが、PNG圧縮bytes／hashのMain Thread時代との一致、大解像度、多数Frame、長時間I/Oまたは実時間cadenceを要求しない。Worker encode失敗時はMain Threadで再試行せず該当Captureを失敗終端し、ゲームを継続する。所有権、Completion順序、Drain／Join、Freeze、final Publication／Recovery契約を維持し、過負荷時はCaptureだけをBackpressureまたはDropする |
| Phase 0.11 | 最小NVENC bounded chunk Backend | D-137～D-147の`NvencBringUpProfileV1`とWindows／NVIDIA／D3D11 WDDM固定の`NvencCaptureEvidenceBackend`をPhase 0.1の非同期境界へ追加する。Tier AはEditor内でFixtureごとに独立したprocess-stateとfake clock／Fence／completion／Publication Service、数Frame、小payloadを使ってlifecycleとfaultを決定論的に検査する。実NVENCはEditor process内で起動せず、Tier Bはstandalone Player／専用process上の短い実NVENC結合、Tier Cだけを同じくstandaloneの30fps／120 tick／4秒提出窓／30秒Finalizationのhardware-qualified nominal、Tier Dを実障害診断とする。GPU TextureからNV12変換してNVENCへ渡し、PNG用RGBA CPU readbackを経由しない。固定2 WorkerはRun開始から並行稼働し、Main Threadも完成済みFrame Completionを継続的にbounded pollする。Submit WorkerがAccepted FIFO順にNVENC submitとrecord生成を行うのと並行して、Output Workerが生成済みrecordを同順に回収する。同Worker内部でOutput Collectorが固定owned領域へcopyしてEncode Sample Slotを返し、同期Run Chunk Sinkへ一方向移譲して単一Run chunkへappendする。第三Worker、Access Unit Queue、独自creditまたはreorder状態を持たない。Capture専用Source Surface、GPU同期、Encode Sample、WorkおよびQueueを各8 in-flightへ固定し、Capture開始前に8組のTexture／Output Buffer／EventとNVENC登録、およびOutput Worker専用16 MiB owned領域1件を一括確保してRun中に循環再利用する。いずれかを確保・登録できない構成は縮退せずUnsupportedとする | Tier C nominalは120件すべてのAccepted／Frame Completion、1件の確定chunk Artifact、120件のFrame Relation、Contextの`Finalized` terminal result、局所Registry slotの`Registered -> Committed`、Plan commit、Publication、CaptureCompleteを要求する。StopAccepting後も両Workerが並行drainを続け、Submit Workerは全Accepted recordを生成して先にjoinし、Output Workerは残存record、全owned Access UnitとSink処理をdrainしてMain Threadが全Frame Completionを反映した後にだけFinalize／Abandon要求を受ける。chunk単位のhash確定、close、rename、terminal result生成・回収後、正常経路では全map／lock解除、登録解除、Output Buffer／Event／Session破棄をOutput Workerの最終内部teardownで完了して同Workerをjoinし、その証拠後にMain Threadが終了境界でUnity管理Textureを破棄する。両Worker静止とMain Thread側Texture破棄を含む資源ゼロ確認後だけBackend全体`TryJoin()`、Trace seal／Plan commitへ進む。Fresh Publicationは専用trusted内部経路でstaging全hashを省略し、固定memoryでfinal全hashを1回だけ行い、Publish Receiptを同一CaptureCompleteへ再利用する。Recoveryは同Receiptを信頼せずstreaming再検証する。制御可能な`Abandoned`または既知pre-commit失敗では通常Plan／CaptureCompleteへ進まず`Incomplete`、rename結果不明または安全な資源状態を確認できない制御喪失ではfileを変更せず、後者はprocess-wideに`PoisonedUntilProcessRestart`として同processのCapture／Recoveryを停止する。Poison RunはTrace Freeze、Summary、正式なIncompleteへ収束しなくてよく、次回processが既存file集合だけから分類する。H.264 High、IDR-only、SPS／PPS反復、P／B FrameとFrame間参照なし、CQP 28を要求設定として記録し、raw bytesを変更しない。`Flush(true)`は要求しない。0 Frame、未確定chunkまたは順序違反chunkは全体を破棄してよい。Tier Cはclean Decoder process 1回で確定chunk全体の120 Frame decode／寸法をstreaming確認し、画素比較は先頭／中央／末尾だけとする。固定2 Workerで30fpsを満たせないかWDDM非同期順序契約を維持できなければreorder／同期Fallbackへ移らずUnsupportedとする。Render callbackはboundedな登録だけで戻る。固定Surface／Work／Completion／chunk容量枯渇はBackpressureまたはCapture失敗終端、Run開始前のverification buffer構成不能はUnsupported、commit前の既知buffer不足はIncomplete、commit後またはRecovery中のbuffer不足はfileを変更しないdeferred経路へ固定する。Capture専用Pool以外へPoison資源を混在させず、Capture subsystemはゲーム停止を要求しないが共有D3D11 Device／Driver自体の喪失後も描画継続することは保証しない。このbounded chunk形式を連続運用へ昇格しない |
| Phase 0.12 | 可変長Trace Writer | D-159と21.16のprivate Writer、producer専用固定容量Payload／Runtime Index Ring、固定Event mask、bounded Drain、stop／join後の単純sealを同一移行系列の内部backendとして実装する | 通常writeに共有locked RMWと実行中allocationがなく、payloadコピー完了後だけRuntime Indexが公開される。lane FIFO、wrap、Index／Payload容量不足、oversize Drop、固定件数Drain、最終Drainを検証し、現行WriterとCPU時間、copy byte、allocation、Dropを比較する。Release既定の切替と旧経路削除はまだ行わない |
| Phase 0.13 | MemoryBounded Paged Trace History | ProfileでPage size／Page数／総容量を決めてRun開始前に確保するPayload Page列、Pageごとの`CommittedByteCount`、History全体の64 bit `CommittedRecordCount`を0.12 backendへ追加する。History Index、Page状態enum、live Snapshotを持たない | `FrameHeaderSize + MaxPayloadLength <= History Page writable容量`と`MaxPayloadLength <= Producer Lane Payload容量`を開始前に検証する。record全体を単一Pageへ書いた後だけcommit値を進め、Page末尾不足、History満杯、確保不能を待機や拡張なしでReject／Dropできる。停止後Viewは全record配列を生成しない。Release既定はまだ切り替えない |
| Phase 0.14 | Trace bundle／format v2切替 | 既存の`bundle.index`、`manifest.json`、`trace.bin`という3 file構成と一時bundle directoryの最終renameを再利用し、単一可変長`trace.bin`、Manifest／bundle v2、bounded逐次Readerを実装する。v1 Loaderは維持する | Pageのcommitted prefixから最終Headerとframed record列をstreaming出力し、同時にhashを計算して全file再読込みを行わない。64 bit件数／長さ、既知／未知Kind、切詰め、overflow、範囲外長、末尾不一致、hash不一致、v1／v2分岐を検証する。成立後に一度だけRelease既定を切り替え、同じ変更系列で旧runtimeを削除する。`Flush(true)`、電源断durability、部分file救済を要求しない |
| Phase 0.2 | pilotベースの用途別Fixture生成・選抜 | 固定Blender、本隊Script／Preset移植、v2選抜schema／Bundle／ZCG v1、5カテゴリRecipe、Generic Geometry／Fixture Binding／StructuralSlabFixture、単一Hull Codec255／Cook Role128、Synthetic Suite、Catalog v2／全EarlyFixtureBatchManifest一括確定／監査／frontier限定Report・Receipt、T-078～T-081 | 10.2.2の最低12 Source、Render6帯中5帯、PhysicsCookInput6 Source、Building2件の4 Slab以上sidecarを満たした連続完了frontierをFreezeする。Licensed少数sampleの意味的等価と公開Synthetic／Goldenのbyte一致を確認し、Entry単位provenance・全Attempt・用途Bindingから下流入力を復元する。Licensed Strict Solid、一般Convex分割、実Cook、製品metadata、全Asset互換、pilot Geometry直接採用、全件再生成は要求しない |
| Phase 0.25 | Cook比較Probe | Phase 0.2のPhysicsCookInput（公開合成4／16／64／128頂点・非公開Licensed単一Hull最大128頂点）、U1 Unity BakeMesh Harness、N1／N2／N3 Native PhysX Harness、工程別Timer、Repository外のManifest／Result／Suite Index Bundle、結果レポート | 製品Geometry完成前の早期Probeとして、同一入力でUnity経路の実費用とNative改善上限をP50／P95／P99まで再現測定でき、N1／N2／N3の必須Stage差、版・設定差、Manifestと実測Resultのhash対応を記録できる。129／255頂点のCodec FixtureをCook入力として流用しない。合成Datasetをcanonical正本とし、LicensedRepresentativeは実Asset傾向の補助確認に限定する。T-076の前提とはせず、Native PhysXを製品Runtime依存にはしない |
| Phase 0.5 | XRスモークテスト | OpenXR、Quest 3S有線Link、Grip Pose、Tracking State、GripToKatanaOffset、Single Pass | 空シーンで両眼90Hzと左右の刀姿勢・追跡復帰を確認 |
| Phase 0.9 | 読み込みとUnity Mesh表示 | 少数の代表AssetをUnity Meshで表示し、同形状InstanceはMeshを共有する。並行光源1つ＋ambient、基本表示・影 | 別Transformの複数配置で共有Meshと影を確認する。切断登録のTopology試験を前倒ししない |
| Phase 0.91 | Unity Mesh表示最適化Probe | 同じ代表SceneでForward、Forward+、Forward+＋GRDを比較し、Mesh／Material共有と実際のGRD適用状態を確認する | Main Threadの描画準備・関連待ち、Render Thread、GPU時間から採用構成と理由を記録する。速度Gate、多数ライト・GPU Occlusionの全組合せ比較は要求しない |
| Phase 0.92 | VP Stage 1 | 明示操作でMesh→CPU AoS／Indexプール→GPU表現へ変換し、Direct・非indexed＋shader-side indexing／属性Pullingで描画する。AcquireReadOnlyMeshData等による取得、更新範囲の集約、両プール追記、Mesh／VP混在と影、複数Geometry参照の選択・表示構成・別Transform Instance | 少数Geometryで変換・混在表示・影・複数参照構成・選択と、内部Metadataで識別した少数Componentを一つの連続Index範囲として描けることを確認し、変換マイクロベンチを取得する。小さい初期GPU容量から1回拡張し、コピー・描画境界での参照切替後も既存表示を保ち、旧参照の更新・退役と投入済みGPU利用の終了後に旧Bufferが一度だけ解放されることを確認する。変換・転送費用と実際の表示開始フレームを既存計測で確認する。実切断・人形切断は要求しない |
| Phase 0.93 | 消去と範囲アロケーション | 表示Instance／Geometry参照退役、4.5.3の予約・公開・寿命管理、退役Indexと未使用・失敗予約のbest-effort再利用。Published Vertex再利用・コンパクションなし | 少数の合成Jobで共通入力読取りと非重複出力への並行書込み、完了後公開、未使用予約回収、参照中Indexの再利用抑止、退役後再利用、予約不足後の再実行、完了回収後のReserved所有権引継ぎ、必要時のCPU読取り公開と別Reservedへの仕上げを確認する。切断器へ依存しない |
| Phase 0.94 | VP Stage 2 | Stage 1のGeometry表現・アロケータを維持してIndirect・非indexed＋shader-side indexing／属性Pullingを実装し、描画要求の集約・引数管理・個別発行を見直す。正負連続Index配置による粒度削減をIndirect APIの自動融合とみなさない | Stage 1と同じ小規模代表Sceneで機能を維持し、実際の発行経路とMain Threadへの効果を比較して採用を判断する。固定改善率・全Scene高速化は要求せず、効果が乏しければ人間判断でStage 1を採用して進める |
| Phase 1 | 即時切断／Dispatch境界 | Phase 0.9～0.94のVP基盤、合成Geometryと点Anchor、正負論理子・切断履歴の公開、単一clip・仮分離・簡易断面、対象ごとの低頂点1 Hull Placeholder、片側空No-op・受付上限、V1 Dispatcher API・固定Queue・予約枠・取消・Completion | 少数の合成入力と選抜済みFixtureで即時表示とT-074の純粋データ公開・Anchor配分を確認する。実Convex切断・Rigidbody・cookと固定物理の結合はPhase 4へ置く。支持を理由に描画を省略しない。未公開親への受付制限、No-op／混雑時の非変更、公開前の子先取り禁止を確認する。製品Preprocessorを前倒しせず、合成Workで後続PhaseがQueue実装型へ依存しないDispatch境界を固定する |
| Phase 2 | 仮断面・影強化 | 表示／Stencil共用基底Geometry、6.2の入力Gate、`RenderCutTopologyMap`、T-084、ゼロKerf、LogicalCutOperation、TemporaryRenderCapRecordSet、OBB交差Cap Bounds Polygon、両眼Frustum／Facing Cull、128初期化の正符号8bit IncrementWrap／DecrementWrap Stencil、Residual Stencil Supportの保守的投影競合、符号保存のCapCompatibility Group、`MaxStencilColors`と最後の統合Color、Color単位Volume／Cap Batch、`TemporaryClipConstraintCandidateSet`、`SV_ClipDistance` 8面＋PS `clip()` 4面＋Renderer-only overflow無視、軽量Camera近傍保護、共通トゥーンの粘土色グレー、処理経路デバッグ色、ShadowCaster用同一Hybrid Clip／Offset、XR両眼対応、Pending Cut／Stable履歴管理、T-067／T-089 | 2～4連続切断と複数対象で、表示とStencil Volumeが同じ合格済み基底／Stable Geometry、Topology、windingを参照し、用途別Geometryや描画時のGeometry再検証を持たない。通常Colorは左右眼の非互換Residual Supportを分離し、Color数を固定上限内に保ち、同じColorでは全Volume後に全Capを描く。Self-intersection、別Topologyの重複／Coincident、Internal／Nested、全体反転を向き保存で受理し、共通入力Gate不合格は切断対象へ登録しない。符号証明、向き正規化、Winding上界、Count容量分割、符号別Groupは作らない。`S=(128+W) mod 256`と`S>128`を使い、範囲外は5.2の品質例外とする。最後のColorでは混入、欠落、余計なCap、誤Depthを許容し、GPU時間は測定対象に留める。8bitを排他利用できない構成ではゲーム開始を拒否し、部分Bitや代替経路を持たない。候補面は古い未Commit祖先制約を優先するdependency-closedなstable順で全Pass／両眼へ共有し、8面をRaster、続く4面をPixelで処理する。超過した後発面は即時Stencil VolumeをsubmitせずCap板と論理／背景処理を残す。Cap pair／Coverage探索、Cap単位Buffer compaction、Mesh部分更新、多段Fallbackを行わない。Color割当ては5.6の実装自由度に従い、全Graph構築を必須としない。Camera近傍は既存Boundsと安価な視界効果だけを使い、5.2の品質例外を許容する。Shadow MapではStencil Capなしの影近似を使用する |
| Phase 3 | 共用表示／Stencilジオメトリ | Job＋Burstの一つの三角形切断系列、4.5のVPプール入力・出力と範囲所有権、`RenderCutTopologyMap`、Topology系譜の交点共有、共通signed-distance分類、有向境界と整合するCap、閉鎖・edge／vertex manifold・局所winding整合の出力継承、Runtime面積0 Triangle許容、正負各最大1のGeometry範囲、単一予約内の正負Index直接出力、CPU完成後の必要転送と現在frameでの表示Commit、GPU範囲更新とメインスレッドGeometry参照公開、両用途の原子的Geometry Commit、空出力の非生成、V1 Dispatcher Class 2接続、T-083 | 少数Fixtureで正負連続Indexと転送1回／再利用時0回を確認し、現在の追従先を使った表示をPhase 4の実cookなしで成立させる。仮表示から共用VP Geometryへ置換し、切断Kernelの重い頂点処理がMain Threadへ戻らない。通常更新で全Job／GPUを待たず、容量拡張時の停止・容量限界での終了だけ4.5.4に従う。箱、凹形、複数閉Component、全体反転、Self-intersection、別Topologyの重複／Coincident／Nested、vertex／edge／face通過、同一点複数port、面積0を含む契約内Fixtureで、生成Capを含む各非空出力が入力と同じ共通契約を継承して再切断できる。表示とStencilが同じ世代・Triangle集合を使用し、用途別の切断、Cap生成、適否、修復、簡易表示Proxyを持たない。Triangle数0の側にはdummy Mesh／Cap／Rendererを作らない。不正結果、世代不一致、出力予約不足は両用途とも公開せず、最後の共用GeometryとPending Clipを維持する。出力予約不足だけは4.5.3の再予約・再実行を許容する。全Mesh self-intersection／inside-outside／shell分類をRuntimeへ追加しない |
| Phase 4 | 物理 | 全体0.5G仮設定、FragmentGroup、PendingPhysicsSplit／PendingAnchoredSplit／ProvisionalPhysicsSplit／ProvisionalAnchoredSplit／StableUnsplit、Phase 1の正負子・点Anchorモデルとの接続、仮描画と物理運動の分離、固定側Impulse禁止、自由側解析仮運動、旧Cooked Convex Resource Lease、Provisional Actor／Shape／Separation Constraint、全外界Collision／Sibling抑止、OBB Provisional質量配分、CanonicalMassBudget、FragmentRenderAnchor初回分裂／物理優先Final handoff、正負各最大1所有者、Native Convex B-rep、Compound内Overlap許容、Count／Write／Validation Job、7.2のRuntime頂点上限・新規交点の内接削減（次数3＋ΔV順は初期実装方針）、7.6のSide集合による物理所属、物理のみの正常な空表示出力、Temporary Physics Proxy生成Kernel／Validation、Job化`Physics.BakeMesh`、Fast Cook初回分裂、選択的Fast Simulation再Bake、Sibling Collider一時衝突抑止、別Mesh差し替え、Upgrade Scheduler、`PhysicsConvexMassWeight`、Convex由来の体積／重心／慣性とOBB／AABB近似、質量保存、速度継承、Generation Reject、既存物理の正式採用、保守的な仮予算管理、Phase 0.2 Convex Fixture回帰、T-070／T-077／T-085／T-086／T-091との差分再確認 | T-005の内接削減成功・不成立と再切断を成立させ、T-061／T-091で削減後形状の再cook・handoffを確認する。7.2の縮小に伴う品質変化を許容し、14章の完了順確認を実際のAnchor配分・所属・物理採否へ接続し、物理先行／表示先行と既存物理正式採用を確認する。物理は表示転送を待たず、cook遅延中も仮表示を維持し、点Anchor配分が完了して所有者単位の固定／動的が定まり、容量内では再cookなしのProvisional Rigidbodyへ原子的に分裂して外界Collisionと連続運動を先行する。交差する独立ComponentをUnionせず、凹形状でも同Sideの島を一所有者へまとめる。専用Convexを持たない非空共用Geometryも7.6のSide規則へ従い、Containment／Coverage／最良適合や後追い精密化を要求しない。Geometryが空でPhysics Convexが非空の出力はRendererなしの通常物理Objectとし、専用型・復旧metadataを作らず通常のObject／Level lifetimeまで保持できる。重複Compoundでも生体積を質量として二重計上せず、Weight継承で切断前後の質量和を保存する。形状、質量またはFinal handoffを成立させられない場合は、利用可能な既存物理表現を正式採用した`Stable Unsplit`へ終端し、時間だけの再試行、小破片消去、質量移送を行わない。Temporary Physics Proxyの実装済み品質がT-077を通り、不正結果は物理へ公開しない。T-076前はSchedule数、Worker占有、Batch、同時Bake、Nativeメモリ、`MaxIncompleteCutOperationCount`へ保守的な試験値を設定する。分類後は固定側を動かさず自由側だけを安全に分離する。公開合成Fixtureと選抜済みLicensed Convex Fixtureの両方で、Convex分割／質量特性／BakeがMain Threadを停止させず、二段階Colliderを安全に昇格する。Unity経路が要件を満たす限り維持し、満たさない場合だけD-086のGateを評価する |
| Phase 4.1 | Geometry／Cook性能Baseline | 固定合成Dataset、Phase 0.2 LicensedRepresentative補助Dataset、Single-Thread Kernel Harness、Job Batch Harness、共用Geometry／Convex／T-077検証済みTemporary Physics Proxy／Bake工程Timer、Repository外のManifest／Result／Suite Index Bundle、P95／P99容量式 | Phase 3／4の正しい製品実装をT-076に従い、公開合成Datasetをcanonical正本、選抜済みLicensed Fixtureを別の非公開補助Suiteとして測定する。各DatasetCaseIdの固定規模軸とSamplesをjoinしてKernel単発µs、Bake／Commit単発Latency、定常Throughput、Job End-to-End latencyを再現する。Suite内DatasetId→DatasetContentSha256一意性、Target×Stage×ExecutionMode、FailureRate／Rejected契約、bounded Manifest／Result／Index Loaderを検証し、Phase 4の保守的仮上限を校正する。O-035／O-039の初期確定予算と斬撃波Deadlineまでに処理可能な対象数を根拠付きで決め、T-070の早期結果を再解釈できる |
| Phase 4.2 | Player非接触Locomotion | Player Layer非接触、現在物理所有者へ追従するPlayerLocomotionOccupancy、Near-Wall Fade、T-088 | Playerが物体へImpulseを与えず、モデル化済みOccupancyへの人工移動の代表的新規侵入を抑える。動く大型物体でForcedOccupancyOverlapを確認し、物理Commitを巻き戻さない。Camera被り・内部視点は既存の限定保証とbest-effort視界保護で扱う |
| Phase 4.3 | 建物由来子のWorld D6と一般外部Joint撤去 | 7.2.2のIsBuildingDerived／BuildingSplitDepth、通常1→2公開でのWorld D6生成、指数Limit、Actor寿命と既存失敗境界への接続、既知Constraint識別、T-094 | 手書きSyntheticで生成・建物由来だけのDepth更新・1物体結果・Final handoff／Upgrade時の維持・構築不能を確認する。一般外部Jointの継承・付け替え・保護・予測を要求せず、4.5／4.6が既知Constraintを識別できる。拘束効果を保証せず、製品Recipe・5.6分割・5.7 GC・未来予測本体を待たず完了する |
| Phase 4.5 | 飛翔斬撃と未来評価／剛体面リベース | Gesture状態機械、Edge Direction Gate、Recovery、NonCutting素通り、Slash Latch、Span／Travel Axis、単調・一価SlashFront、逆行／自己交差Finalized、前縁VFX、帯状Sweep、Candidate Flight Bounds、評価DAG、V1 DispatcherへのReady投入、先行切断、Commit検証、O(1)固定刻み直接予測、`DirectRigidPredictionEligibilityGate`、対象外の後追い処理、T-017の直接予測部分、19.5.1の対象別Local Plane選択と7.6の受付判定／T-093／固定Trace束 | 復路とU字軌道で二重前縁や誤斬撃を作らず、Latch直後から三日月前縁が飛翔・命中し、Extending中も前縁が成長しながら進み、遠距離対象の多くが接触時に有効な準備済みVP Geometryへ即移行する。DAGはDispatcher内部表現へ依存せず、Schedule前取消と世代RejectでV1へ接続する。T-017の直接予測部分で本体統合と受付Gateを確認し、対象外は後追い処理へ戻す。T-093で、面採否と片側空／容量受付がJob Readyに依存せず同じSnapshotと採用面を使い、受付済みのTemporary／Provisional／Stable／Finalが同一面へ収束し、実Actorを巻き戻さないことを確認する |
| Phase 4.6 | 予測拡張 | 局所PhysicsScene、T-017の局所Physics部分、対象Stepへ解決済みの`ExplicitAnimationStateV1`、Loop／Clamp Clip Catalog、`ResolvedAnimationPoseInput`／`FutureAnimationPoseEvaluator`境界、controllerなしPlayable／Pose Table比較Probe、random access未来Rig Pose、Asset／Evaluator Identity、信頼度別フォールバック、T-018 | T-017の局所Physics部分で19.4の接触・転動対象との統合と、必要な動的近傍の未対応Constraintによる後追い処理を確認してT-017全体を完了する。AnimatorController rolloutなしで任意`FixedStepId`のPoseを評価し、現在表示と未来評価が同じ明示StateとCatalogを消費する。PlayableとPose Tableの代表骨誤差・Main Thread／Job費用を比較でき、Mode／duration／Identity不一致では実姿勢Fallbackへ移る。V1予測対象のプロシージャルIKと左右反転は双方で無効 |
| Phase 4.65 | 未来予測用Jobベイクの比較・採否 | 4.5.2の不変Rig Pose＋共有source skinning入力から共通CPU側VP入力を生成する限定実装と同期経路の比較 | 少数の対応済みSkinned入力と固定Poseで品質・非同期回収・Main Thread負荷を14章／21.2に従って比較し、人間が導入の採否を決める。効果がなければ不採用も正常完了。本体DAG／VPプール接続と人形切断の完成は不要 |
| Phase 4.7 | モブ未来計画 | `MobTrajectoryKernelV1`、Waypoint／Lane Desired Motion、FixedStep同期二相更新、固定長Trajectory Queue、Near Live／Mid・Far Playback、MobPlan／PlanGeneration、`AnimationPlannerV1`、Rootと同一epochの`ExplicitAnimationStateV1`全体、粗い全Plan／Group無効化、V1 Dispatcher背景補充、Trace、T-092。Jobベイク採用時だけ既存DAG／VPプールへ接続して候補の未来Pose／VP入力を準備する。ORCA／Chunk／依存Graph／Flow Field／Pose Layer／Mirrorは成立条件外 | 同じKernelが現在更新と未来RootTrajectoryを生成し、同じAnimation Plannerが現在／未来State全体を生成する。介入なしのMid／Farモブで計画再利用と介入時の旧Generation無効化を確認する。Jobベイク採用時は準備要求・結果取得・失効も確認し、人形の先行切断完了率・最終Commit率は5.1へ分ける。不採用時は計画単体で完了する。Queue枯渇・固定容量超過でもState全体のHoldを許容してMain Thread Spikeや古いPose／準備結果の採用を起こさず、モブ同士の多少の重なり、IK／左右反転なし、V1 Clip hard switchのPose popを初期品質として許容する |
| Phase 4.8 | OpenXR Projection Capture＋正式録画判断 | `OpenXrProjectionCaptureProfileV1`、Windows API Layer、D3D11固定、SDR、MSAAなし、Dynamic Resolutionなし、Single Pass、Projection 1枚、左眼45fps、Release前GPU Copy、固定Profile検証、GPU Encode、Capture Record／Run Manifest同期。Phase 0.11の120 Frame上限を外す候補ではRegistry／Publication Planの計算量、正式chunk長、GOP／Container／segment、durability頻度、index／seek、保持期間、payload所有権／copy／hash回数、停止時Publication時間、詳細画質評価を追加する | 切断PoCの異常をProjection画像とTraceで再現調査でき、想定外構成はFail Fastし、非録画時との差が性能予算内。連続録画を採用する場合はPhase 0.11のbounded Run chunk形式を暗黙流用せず、T-054実測後の正式形式で容量・停止時間を満たす。不要ならAPI Layerまたは連続録画の導入を個別に見送れる |
| Phase 5 | 人形の基本切断 | 命中時実Bone Poseスナップショット、現在Poseの同期BakeMesh→CPU取得・VP変換、身体・衣服・髪の適合済み各Skinned Componentに対する一つの共用切断／Cap生成系列、骨proxy分類、物理移行、T-008 | 先行準備なしで基本動作中のNPCを任意方向に切断し、各Componentの同じcanonical posed positionと実Capを表示／Stencilへ共用する。代表入力の準備費用と表示開始フレームを測り、4.5.2の同フレーム表示目標を確認する。開放装飾を身体の別Shellで救済せず、Jobベイクや先行成果物の採用成功を完了条件にしない |
| Phase 5.1 | 人形の先行切断統合（Jobベイク採用時） | 4.65・4.7・5の接続、未来用Jobで準備したVP入力・切断成果物の実命中時再利用、既存Geometry／Physics Commit | 14章の既存試験の統合として、有効結果採用、未完成・Pose不一致時の通常同期経路、世代失効後の回収を確認する。実姿勢から後追い処理へ移り、Animator内部Stateを巻き戻さない。同時候補の負荷と先行完成・採用状況を21.2で測る。4.65で導入見送りを決めた場合は本Phaseを省略できる |
| Phase 5.5 | Asset自動前処理 | Phase 0.2の選抜Report／失敗例を入力に、完全なPortable Blender Manifest／Bootstrap、固定版ヘッドレス実行、Asset別Recipe、表示／Stencil共用Cut Geometryと`RenderCutTopologyMap`、必要な幾何Topology Metadata、Component単位の閉鎖・manifold・局所winding整合、見た目を保つReduction、UV／Material再構成、点Anchor入力、建物由来Metadataと初期Depth、Compound Physics Proxy／finite正和MassWeight、検証、キャッシュを実装する | Phase 0.2でRejectした複雑Assetも対象に含め、代表家具・車・建物を別PCでもGUIなしで再現生成する。相互に食い込む閉ComponentをBoolean Unionせず共用Geometryへ通し、FBX control point／Import topologyからattribute seamを越える安定IDとcanonical posed positionを生成し、6章の共通入力Gateに合格したGeometryだけを切断対象へ公開する。開放Boundary、局所winding不整合、edge／vertex Non-manifoldはAsset修正またはRecipeで解決し、解決しない入力を切断対象外とする。用途別Stencil Shell、小部品専用分類・消去用ID・Shard、符号証明、signed-volume分類、向き正規化、Winding上界Metadata、Runtime修復を生成・保存しない。専用Convexを持たない共用GeometryはRuntimeの7.6で既存Convexへ所属させる。製品用Strict Solidを生成・検証・Fallbackせず、その成功を代表Assetの合格条件にしない。Phase 1／4の合成入力を実AssetのGeometry・Convex・点Anchorへ接続し、Phase 0.2より広いAsset範囲と製品品質を達成する |
| Phase 5.6 | 追加空間分割（任意） | 7.9の単一平面探索・範囲内部の面配分、新Index領域への振り分けコピー・必要転送・旧領域Free、既存Convex処理・cook・質量・点Anchor、7.2.2の建物Depth・子D6生成と親D6退役、非命中公開、全体1未回収試行と入力別抑止 | 大きなIndex範囲内の離れた部分を2物体へ分け、Vertex共有と読者寿命後の旧Index回収を確認する。成功後は通常再切断でき、不成立・無効時は元物体が通常完成状態で残る。通常切断とGCを依存させず、7.9.5の共有資源競合による遅延を許容する。Phase自体を省略可能 |
| Phase 5.7 | 表示なし物理物体の遅延回収（任意） | 7.9の確定空判定、物理所有単位の登録終了、既存Actor／Shape／システム所有Constraint／Job資源退役への接続 | 7.9.7のGC選択・接触中退役・共有資源寿命・所有D6の一度だけの退役・後着成果物拒否を確認し、無関係Siblingを維持する。追加分割の実装・有効化・成功へ依存せず、Phase自体を省略可能 |
| Phase 6 | コンテンツ | Synty City街区、10プロップ、単一並行光源＋ambientでのシェーダ統一、既製モーション | 垂直スライスとして一連の遊びが成立 |
| Phase 7 | 実測後最適化 | 端末別品質、破片LOD、V1 Dispatcher Counter／T-076結果に基づく必要最小限のV2候補、遠距離確定、ストレス試験 | ターゲット実機で性能予算を満たす。費用学習、aging、動的優先度、work stealing等は実測で必要性が示されたものだけを追加し、不要ならV1を維持する |

Phase 0.9～0.94はPhase 1より前に実施する。Phase 1.0という呼称も同じPhase 1を指し、既存Phaseは一括改番しない。各0.9xは先行成果を使いつつ独立に完了でき、即切断、実切断、Cap、物理切断、未来予測の完成をGateにしない。Stageは描画方式の段階でありPhase番号とは別である。0.94の実装・比較は必須とし、採用結果に応じてPhase 1へ進む。実行時自動Fallbackを追加しない。

0.92はMeshデータ取得、AoS変換／CPUコピー、GPU転送発行を分けて軽量計測し、SetData呼出し時間を実GPU処理時間と混同しない。初回確保・容量拡張は通常変換と別に測り、新たなBenchmark Dataset／Schema／保存基盤を作らない。

Phase 3では4.5.6の正負直接Index出力・転送・現在frameでの公開を実装する。切断Kernelの意味・Topology・Cap品質・世代の有効性・表示／Stencil同時公開は維持し、Final物理所属の確定をIndex配置・転送の前提にしない。詳細layoutやKernel内部の書込み方式は必要な段階まで未決とし、物理用のCount／Write／MeshData／Physics.BakeMeshは変更しない。Phase 4.5の剛体先行計算と、4.5.2の人形経路分離も同じ出力と準備済み範囲再利用へ接続する。0.92へAnimation／Pose Evaluatorを前倒ししない。Stage 3とGPU並行ベイクは実測に応じた後段最適化に残す。

Phase 4.6は既存T-017／T-018による局所Physicsと未来Rig Pose評価で完了し、Jobベイク・人形切断の完成を要求しない。4.65は4.7や5を待たずに限定実装・比較・採否決定まで行い、Phase 7へ先送りしない。導入採用時は4.7で本体接続とVP準備、5.1で人形先行切断統合を確認する。不採用時は4.7のJob依存部分と5.1を省略して先へ進め、未完了負債にしない。5は採否に依存せず同期経路で独立完了できる。4.65／5.1の少数Fixtureのために5.5の完全な製品Preprocessorを前倒しせず、4.8・5.5以降・0.9xの範囲と4.6内部の分割は変更しない。

Phase 1では同じ親にOperation未公開のPending Cutがある間、その親と仮表示領域への後続切断を受付けず、無関係な確定済み対象の受付は継続する。Phase 1で純粋データの確定子と`LogicalCutOperation`を公開した後は、Geometry Commit／cook／Physics未完了でも公開済みの子を再切断できる。Operation公開前の終端失敗では4.2の限定退役を行い、新しい暫定Fragment ID、Side Path、専用Queue、再試行、代替GeometryをPhase完了条件へ追加しない。

Phase 1は手書きのLogical Convex Cell参照、finiteな点Anchor、Fragment Physics Frame内local位置を使い、CellのAnchor集合への半空間継承、所有単位内にAnchorがあれば固定・なければ動的とする判定、正負の確定子とOperationの原子的な公開を純粋データで確認する。Phase 4は同じ規則を実Convex系譜、旧Cooked Geometryを共有するProvisional、Final Shape引継ぎへ接続する。Phase 5.5は固定支持を設定する対象AssetのRecipeで初期Cellと点Anchorを生成する。Phase 0.2のNoFixedSupport成果物は変更せず、その再生成や公開Schema変更を要求しない。

Phase 4のcook待ち標準経路はD-132のProvisional Rigidbodyとし、点Anchor配分と所有者単位の固定／動的の導出後のActor／Shape／Constraint生成、旧Cooked Geometry共有、OBB質量近似、外界Collision、物理Actor pose／速度不変のFinal handoff、由来Convex half-space包含検証をT-091まで実装する。Anchor継承に必要なWorkの未完了・実行待ちまたは受付済み処理の一時的な実行枠不足は7.1／7.7の既存Pendingで継続し、要求構成・Backend共有・資源確保・原子的構築・Finalを既存の安全規則で成立させられない場合だけ7.6の既存物理の正式採用で終端できる。Provisionalを現時点で生成できないことだけで後続のFinal物理処理を打ち切らない。Provisional専用再cook、部分Actor公開、同期cook、Final Commit時の物理Actor pose／velocity補正はPhase 4完了条件に含めず、表示側の瞬間的な追従差は許容する。

Phase 4では公開前Fallbackと公開後Faultを別経路として実装する。公開前失敗は実状態を変えず`ProvisionalPhysicsFallbackActivated`へ要求種別だけを記録し、公開後異常は固定容量の直前finite物理Snapshotを用いてGroup全体を`ProvisionalFaultFrozen`へ不可逆遷移させる。後者は物理安全Classで次のDispatch対象とし、部分Freeze、旧Group rollback、自動Dynamic復帰、Final物理Commitを行わない。

Phase 4.3はPhase 4.2の後、4.5の前に置く独立Runtime Phaseであり、章番号4.3とは区別する。汎用物理のPhase 4、既存Baselineの4.1、Player非接触の4.2の完了条件へ建物D6を混在させない。Phase 4.5／4.6はその既知Constraint識別境界を使用する。

Phase 5.5の建物Recipeでは、外周Structural Slab、入口回避Compound Box、必要な点Ground Anchorに加え、IsBuildingDerived=trueとBuildingSplitDepth=0を生成する。その他の初期所有者はfalse／0とする。Phase 0.2は10.2.2のGeneric Geometryと薄いStructuralSlabFixture、Slabごとの単一Boxだけを供給し、Fixture用設定と製品metadataを区別する。

Phase 5.6／5.7はPhase 5.5より後に置く独立した任意Phaseであり、双方または片方を省略してPhase 6へ進んでも未完了負債にしない。専用前処理・Runtime・試験をPhase 1～5.5へ前倒しせず、7.9.7を実装する機能だけに適用する。

## 16. 垂直スライス受け入れ基準

Temporary Stencil Capの見え方に関する本章の受入れ基準には、5.2の明示的品質例外を適用する。物理Convexの内接削減による接触・形状・No-op・質量特性の変化には7.2の許容を適用する。即時表示は4.5.2の必要なベイク・VP変換後に始まり、残る準備費用による表示開始の遅れを許容する。4.5.4のGPU容量拡張に伴う停止と容量限界での開始拒否／終了を許容し、無制限の切断寿命を要求しない。固定状態による仮描画省略を行わない費用、7.9の任意分割でのIndexコピー・新旧範囲共存を許容する。実断面Geometryの品質、支持・物理の安全条件、世代の有効性は緩和せず、表示Commitは4.5.6の現在frame・参照・転送条件に従う。

- 刀の高速移動でも代表プロップを安定して切断できる。

- 必要なVP準備後に始まるclipと仮断面が両眼で一致する。両側が固定でもclip／Capを維持し、固定側のOffset／Impulseは0とする。幾何学的なFrustum／Facing Cullと8＋4面の既存制限を使い、支持による表示状態・再有効化待ちを持たない。

- 通常断面は全体と同じトゥーン陰影の粘土色グレーで統一され、仮断面から実断面への差し替えで特殊な質感変化が見えない。

- 即時切断物体のShadowはカラー表示と同じclip／分離Offsetに追従し、両面Shadow近似からStable実断面の片面Shadowへ移る際に目立つ影の跳びがない。

- 左右眼の一方だけで非互換な可視Cap Boundsが重なる複数の即時切断対象は通常Colorで分離され、OBB投影が重なっても両眼の可視Cap Boundsが非交差なら同一Colorへまとめられる。5.2の明示的品質例外を除き、別物体のStencilによる仮断面のはみ出しがない。Conflict Graphの明示構築は要求しない。

- 同じ全切断面とキャップ状態を共有する対象は重なっても同じStencil Colorへ統合され、別々に動いてWorld Planeが変わったフレームでは自動的に別Groupへ分かれる。

- 両眼とも裏向きのCap GroupはStencil処理ごと省略され、片眼だけ可視または切断面近傍では省略されず、頭部微動で仮断面が点滅しない。

- デバッグモードでは赤＝即時仮断面、青＝先行Commit、緑＝命中後計算CommitをTraceと一致して識別でき、Stable後は通常グレーへ戻せる。

- 表示CPU出力と必要転送が揃い、現在有効なframe／参照へ追従できれば描画境界で共用VP Geometryを公開し、対応Temporary描画を回収する。Final cook・物理所属待ちで転送を止めず、後着物理への参照切替でIndexを再配置しない。物理も必要なCPU情報と適用条件が揃えば表示転送を待たずCommitし、仮表示は現在物理に追従する。既存の容量処理と明示的品質例外は維持する。

- 表示／Stencil共用VP Geometryと物理Convexの切断、検証、cookingはJob＋Burst主体で実行され、Main Threadには通常命中の同期入力準備とBackendに応じたPose評価、表示VPの範囲管理・GPU更新・参照公開、物理Mesh公開とCollider／Rigidbodyの境界Commitが残り、導入採用時の未来の頂点スキニング・VP形式変換は4.5.2のJob経路で処理する。通常更新では未完了Jobへの強制`Complete`によるフレーム停止がない。

- 点Anchor配分と所有者単位の固定／動的が定まった切断はFinal cookを待たず、旧Cooked Convex Geometryを共有する子別Provisional Rigidbodyへ物理ステップ境界で原子的に移行する。外界Collisionを全て有効、同系譜Siblingだけ無効とし、交差Shape由来の早い接触とGhost Contactを許容する。OBB近似によるProvisional質量和を保存し、Final handoffではActor pose／COM線速度／角速度を変えず、由来Convex内へ収まるFinal Collider／COM／inertiaだけを同一Actorへ置換する。表示の瞬間移動を許容する一方、Colliderのpose補正による新規penetration、二重分離Impulse、同期cookを生じない。部分公開は行わない。Anchor継承に必要なWork未完了、一時的な実行枠不足、および成立不能の区別は7.1／7.6／7.7に従い、前二者は既存Pendingで必要な処理を継続する。既存の安全規則に従って成立不能として既存物理を正式採用する場合だけ`Stable Unsplit`へ終端する。

- 公開済みProvisional Actorの非finite、速度／角速度超過、Constraint runtime破綻はGroup全体を`ProvisionalFaultFrozen`へ一度だけ遷移させ、直前finite物理姿勢で全ActorをKinematic化するか全Actor／ShapeをSceneから除外する。部分Freeze、Snapshotの表示補間利用、自動復帰、Fault後のFinal物理Commitを行わず、Trace失敗でも安全状態をrollbackしない。

- 共用Geometry、Convex、T-077検証済みTemporary Physics Proxy、cookの固定DatasetベンチマークがRelease／Burst環境で再現でき、Single-Thread µs/op、Job定常Throughput、End-to-End P95／P99からWorker予算、同時切断数、Batch Size、同時Bake数を説明できる。単一DatasetCaseIdの規模軸、工程別Stage、許可されたExecutionMode、Manifest／Result hash、Samples／Aggregate件数をSuite Indexから検証でき、同じManifestへのResult差し替えを拒否する。同一Suiteでは各DatasetIdが厳密に1つのDatasetContentSha256へ対応し、異なるhashの系列を容量式へ混在させない。Manifest／Result／Index Loaderはそれぞれ64 KiB、64 MiB／100万Sample、64 MiB／10万Entryのschema上限と呼び出し側のより小さい上限を配列確保前に強制する。対象処理の失敗をFailureRateへ残し、計測不能な試行だけをRejectedとする。既存TraceRunManifest／bundleのCodecとGolden Hashは変化しない。

- Unity `Physics.BakeMesh`とNative PhysX比較Probeの入力、版、設定、工程別結果が再現可能に保存され、倍率差だけを理由にNative Backendが製品へ混入しない。Native再検討時はD-086のGateを満たした証拠を残す。

- Operation未公開の親と仮表示領域への後続切断は、無関係な確定済み対象を止めず、状態・世代・先行仕事を変えずに見送られ、保存・再実行されない。Operation公開後はGeometry Commit／cook／Physics待ちでも公開済み子を再切断でき、古いジョブ結果で形状が巻き戻らない。Operation公開前の終端失敗では当該Pending Cutと仮表示だけが退役し、親の現在有効な共用Geometry、先行Pending制約、履歴、物理姿勢・速度を維持したまま新しい切断を受け付け、失敗した切断の後着成果物を公開しない。

- Phase 5では先行準備なしの同期経路で移動中のNPCを切断し、姿勢固定から剛体破片への移行が成立する。Phase 4.65で未来用Jobベイクの限定実装・品質と負荷の比較・採否決定を完了し、導入採用時だけPhase 5.1で先行成果物採用・未完成／成果物不採用時の通常経路・失効回収を統合する。導入見送り時は人形の先行準備による命中時負荷削減を受入条件にせず、4.5.2の通常同期経路と表示開始の許容を使う。数値差・対応範囲は4.5.2、最小確認は14章／21.2に従う。

- 代表的な連続切断シナリオで目標フレームレートとメモリ予算を満たす。

- Phase 0.2は10.2.2の全成果物契約とT-078～T-081を満たし、5カテゴリの最低quotaを満たす連続完了frontierをReceipt確定する。Reportから全予定処理の終端・tagged出力、Indexから用途Binding・Geometry／sidecar・親closure・Entry provenanceを復元できる。Licensed少数監査sampleは事前tolerance内、公開Synthetic／Goldenはbyte一致とする。pilot直接採用、全Licensed再生成、Licensed Strict Solid、一般Convex分割を要求せず、未確定・quota不足・重大監査IssueのRunを下流が受理しない。ライセンスGeometry／画像／対応表は非公開に限定する。 必須契約は実装前にDESIGNへ同期し、完了時は補助実施計画のcandidateを削除してもDESIGNと実装／Schema／Golden／正式provenanceだけで下流読込・再生成・再開が成立することを確認する。

- Phase 5.5では10種類のアセットが、Blenderヘッドレス処理によって表示／Stencil共用Cut Geometry、切断用Topology、Compound Physics Proxyの自動またはRecipe駆動工程を通過する。製品用Strict Solidは生成せず、その成功を代表AssetまたはPhase 5.5の合格条件にしない。

- 共用Cut Geometryはfiniteかつ参照有効で、各Topology Edgeに逆向きの2面、各Topology Vertexに一つの閉fanを持つことを入力準備時に一度検証する。Self-intersection、別Topologyの閉Component間のIntersection／Overlap、Internal／Nested／Coincident、全体反転を許容し、表示とStencilが同じGeometry、winding、Topologyを使用する。不合格入力は切断対象へ登録せず、不正出力は両用途ともCommitしない。用途別Geometry、Runtime修復、符号証明、正規化、Winding上界、Count容量分割を持たない。仮Capは専用Stencil Byteの全8bitを排他利用できる構成だけで`S=(128+W) mod 256`と`S>128`を使用し、成立しない構成ではゲームを開始しない。各Physics Convexは別契約の閉凸形状として自己交差、面反転、退化のない検証に合格するが、Compound内の別Convex同士のIntersection／Overlapは許容する。全成果物は同一入力・Recipe・Blender版から再現可能に生成される。

- 相互に食い込む部品や凹形状、同Sideの離れた島を含んでも、通常切断は正負それぞれ最大1論理子・最大1物理所有者とする。島ごとの独立剛体化を行わず、一体運動・接触力共有・固定時の空中浮遊を許容する。同じCut条件を共有するCapはGeometry Unionなしで符号を保存して同一Stencil互換Groupへ入り、`sum(W_i) > 0`として描画される。正逆相殺による欠落は5.2の品質例外とする。

- 固定支持を設定する入力は、必要な点FixedSupportAnchorについて初期Logical Convex CellとFragment Physics Frame内のfiniteなlocal位置を明示する。現在所属をCellのAnchor集合で表し、切断ごとに正側／負側／OnPlane両側へ継承する。所有単位内に継承Anchorが一つでもあれば全体を固定し、なければ動的とする。他Cell、旧Cooked Geometry共有、頂点Buffer、内接削減や表示Geometryの反対側所属からAnchorを新設・移送・復活させない。必要なWorkは既存依存で待ち、不正入力・世代不一致は既存規則で不採用とする。

- 切断対象として採用するGeometryが6章の共通入力契約に合格する。修正を行う場合は採用した前処理Recipeの範囲で確認し、修正しない不合格入力は切断対象外にできる。未採用の修復方式の実装を完了条件にしない。

- Pending Cut受付前の現在採用Convex分類で片側候補が0件の命中は正常なNo-opとなり、対象状態、世代、表示、物理、履歴を変えず、CutOperationId、Pending Cut、LogicalCutOperation、子、境界、Pending仕事を作らない。全対象の未完了切断数が`MaxIncompleteCutOperationCount`へ達した場合も新規対象を同様に見送り、要求を保存・再実行しない。両者をProfiler Counterで区別できる。受付成功時はCutOperationIdとPending Cutだけを公開し、即時表示はその採用面を使う。確定した非空子と実在境界のValidator合格後にだけFragment、CutBoundaryRecord、LogicalCutOperationを原子的に公開し、確定前に境界履歴を作らない。

- 受付後に確定した非空共用Geometryは7.6のSide別Convex集合の正常な空／非空だけで物理所有者へ所属する。両側非空なら各Side、片側正常空なら正負とも残る側へ所属し、面・Side・Capを変更しない。Containment、Coverage、最良適合、候補探索、専用Convex、極小Rigidbody、質量移送を要求しない。Geometryが空でPhysics Convexが非空の側はRendererなしの通常物理Objectとなり、cook・質量処理を省略せず通常のObject／Level lifetimeまで存在できる。失敗・未計算・Weight不正を空集合へ読み替えない。

- 重複するCompound Physics Convexを含む対象でも、Final物理では生のConvex体積を単純加算せず`PhysicsConvexMassWeight`をLocal ID順binary64左畳みで正規化して、切断前Rigidbodyと全子Fragmentの質量合計を一致させる。Provisional物理もOBB切断近似または等Weightで親Canonical Mass Budgetを保存するが、その近似COM／inertiaをFinal値へ流用しない。全Weight 0／非finiteはFinal Commitせず、各物理Commit対象Fragmentもfiniteかつ正のWeight和を持つ。Weight 0 Convexだけの子は空集合とみなさずGeometryを保持し、独立物理が成立しないため利用可能な既存物理を正式採用した`Stable Unsplit`へ終端する。質量0／任意最小質量Rigidbody、部分的なFinal Commit、Siblingへの質量移送、任意の質量再配分を生成しない。密度1慣性を`assignedMass / convexVolume`でscaleする。共用Cut Geometry／Strict Solid／Convex Boolean UnionをRuntime質量計算へ要求せず、重心・慣性はConvex由来のWeight付き近似または規定のOBB／AABB近似からfiniteかつ正の値を得る。

- Synty／Poly Pro Universe入力と派生した共用Cut Geometry／Physics Proxyが公開Git履歴、公開CI Artifact、公開キャッシュへ含まれない。

- 飛翔斬撃波の到達時刻と候補列挙が再現可能で、静止対象では接触前の先行切断が安定して成功する。

- 刀を十分に振った時点で切断面と初期SlashFrontが振り終わり前にLatchされ、三日月VFX、前縁Sweep、近距離対象の即時反応が同じフレームから始まる。

- 刃側を先行させる広い角度の振りは切断でき、同じ刀向きの復路・峰側移動ではSlashが発生しない。

- NonCutting、Recovery、追跡無効中の刀が地形、プロップ、NPCへ衝突応答せず完全に素通りする。

- Quest左右コントローラのGrip PoseとBladeFrameが一致し、追跡復帰時に誤Slashを生成しない。

- Latch後に軌道を変えても既確定面、生成済み前縁、命中が巻き戻らず、Extending中の追加辺を含むVFX前縁と衝突時刻が一致する。

- Finalized後は折れ線への追加だけが終了し、完成した三日月前縁が最大距離または寿命まで飛翔・命中判定を継続する。

- U字または明確な折返しを含む刀軌道でも、同一SlashFrontが前後二重や自己交差を作らず、生成済み前縁を保ったまま逆行地点でFinalizedする。

- 予測が外れた場合も4.5.2の必要な現在Pose入力を準備して即時切断レンダラへ接続し、古い成果物をコミットしない。

- Quest 3Sの有線Quest Link環境で、頭部追従だけでなく剣、切断、破片を含む実アプリの両眼描画が原則90fpsを維持する。

- 任意の`SlashId`から候補検索、予測、各切断Task、検証、Commitまたは破棄までをEditorタイムライン上で追跡できる。

- Nearのライブ更新とMid／Farの計画済み軌道が同じ固定ステップ移動Kernelを共有し、Current／Future表示は同じゲーム側明示Animation Stateを交換可能なPose Evaluatorへ渡す。AnimatorController rolloutへ依存せず、Jobベイク採用時は遠距離モブのRoot軌道とAnimation Stateを人形の切断先行計算へ利用できる。プレイヤー介入時は旧`PlanGeneration`の軌道・Rig Pose・切断成果物が適用されず、Queue枯渇時も古い軌道を無期限に再生しない。

- Unity Editor更新時にプロジェクトを作り直さず、専用ブランチで固定テストとXRスモークテストを実行し、不合格なら旧固定版へ復帰できる。

- 不変条件違反時に直前30秒を目安とするTraceが保存され、Editorで再読込して原因系列を調査できる。

- PoCでは選択的な片眼映像または静止画をFrameIdからTraceへ対応付けられ、録画停止時と比較して90fps性能判断を歪めない。

- OpenXR API Layerを有効にした検証では、D3D11固定Capture Profile上でProjection画像と`predictedDisplayTime`、Pose、TestRunId、Slash／Object／Task IDを一意に関連付け、API Layer自身のGPU／CPU負荷も別計測できる。Profile逸脱時はゲームを止めず録画だけをFail Fastし、Run Manifestへ理由と実構成を残す。

- 大型建物は外周Structural Slabと少数Compound Convexで切断でき、点Anchorを失った所有単位は通常の動的物理へ進む。建物由来の動的な1→2分裂子には7.2.2のWorld D6を生成・維持できる。落下・横倒し・完全倒壊を引き続き許容し、D6の実際の拘束効果やSolver品質を合格条件にしない。一般外部Joint付き物体は製品切断対象に含めない。

- Player Body／Handはプロップ／破片へ物理Impulseを与えず、人工移動はモデル化済みPlayerLocomotionOccupancyへ新規侵入しない。HMDの実空間移動ではCamera位置を強制変更せず、既存の簡易Volume／Boundsで検出できた場合だけ安価なbest-effortの視界保護を行う。過剰反応、見逃し、未登録または非干渉物体のCamera被り、物体内部視点、即時Stencil処理中の部分Cap／Cap欠落／余計なCap／Stencil混入／左右眼差を許容し、それらを切断・物理・Geometry Commit失敗へ昇格しない。刀とSlashFrontによる切断Interactionは非接触化後も成立する。

7.9の任意分割・物理GCを実装する場合の追加受入条件は7.9.7とする。無効時の通常切断維持を確認し、有効時は成功分割後の通常再切断とGC退役の非復活を確認する。未実装・無効を垂直スライス不合格にしない。

## 17. Codexでの継続更新ルール

- 決定が変わった場合は既存行を消さず、状態を『廃止』にして代替決定IDを記録する。ただし、未実装のTemporary Stencil Capと表示／Stencil共用Geometry、その直接の入力・切断・試験・Benchmark契約、および撤去した旧小破片／Render―Convex品質分類／Shared Convex解決／GPU Debrisとその専用Decision・状態・ID・前処理・Trace・試験・Phase契約に限り、旧仕様を削除・置換してGit履歴だけに残してよい。また、人間承認済みの正負二集合化に伴い撤去する接続Graph、Attachment、間接支持、支持由来の表示状態・Cull、建物専用Safety Tetherと、その専用前処理・保存読込・Schema・Validator・設定・エラー・Fallback・Decision・用語・試験・Trace・Phase契約は、互換用の空表現や旧Readerを残さず削除してGit履歴だけに残す。さらにD-165で撤去する一般外部Jointの継承・付け替え・保護・予測と、その専用の試験・用語・Phase記述も削除してGit履歴へ残す。この撤去にProvisionalSeparationConstraintとBuildingWorldD6Constraintを含めない。専用IDの欠番は許容し、廃止行や対応台帳を追加しない。この限定撤去を一般のCapture／Trace基盤、残る物理・資源寿命・Anchor・世代・Commitへ拡張せず、既存の永続IDを別意味へ再利用しない。

- 未決事項は結論、根拠、決定日を追記して決定事項へ移す。

- 技術検証は測定環境、再現手順、数値結果、スクリーンショット／Profiler参照を残す。

- ロードマップのPhase完了条件を満たす前に次Phaseへ進む場合は、既知の負債として記録する。ただし、独立した任意Phase 5.6／5.7は双方または片方を省略してPhase 6へ進め、未完了負債にしない。

- 新しい機能提案は『即時応答』『幾何精度』『物理整合』『性能予算』のどれへ影響するかを明記する。

- DOCXを再生成せず、このMarkdownのみを正本として更新する。

> **次の推奨アクション** Phase 0として非VR固定テストと共通切断入力に加え、ProfilerMarker、Flow Event、固定長TraceLogger、最小Editorタイムライン、FrameId付きの選択的静止画／片眼録画を先に用意する。まず公開合成箱で性能基準、完全なWork Item／Job時系列、対応画像を取得する。Phase 0の完了条件を変更せず完了させた後、Phase 0.1で既存Unity PNG EncoderをWorkerから使う単一路線へ移し、encodeからdurable stagingまでをMain Threadで待たない。続いてPhase 0.11でnominal 120 Frameとfault系最大16 tickの最小NVENC Backendを、1 Run 1 bounded chunkとして既存Artifact／Publication／Recovery／CaptureComplete経路へ接続する。Main／Render ThreadではNVENC／GPU完了を待たず、固定Surface PoolからBackpressureする。その後Phase 0.2で10.2.2のcanonical基盤を先に固定し、pilotから本隊へ移植した最小Scriptとカテゴリ別Presetで、Generic Geometry／用途Binding／薄いSlab sidecar／単一Hullを生成する。均衡batchと少数監査sampleから最低quotaを満たすfrontierをReceipt確定し、既存Artifactを再利用して全件再生成を避ける。Cap Loop等の既知正解は別のSynthetic Watertight Test Fixture Generatorで用意し、実AssetからStrict Solidを生成しない。続いてPhase 0.25のCook比較Probeを合成Convex正本とReceipt検証済みIndex hashで識別したLicensedRepresentative補助Datasetで実施する。Phase 0.5のXR確認後、Phase 0.9～0.94で共有Mesh表示、描画比較、VP変換、範囲所有権と再利用、Stage 2実装・比較を順に成立させる。続いてPhase 1で即時切断と同時にV1 Dispatcher APIと固定容量Queueを合成Work Itemで固定し、後続PhaseのJobを順次接続する。高度なSchedulerは先に作らず、T-076と実機Counter後に必要な機能だけPhase 7で追加する。OpenXR API Layerと製品用連続録画形式は切断PoC成立とT-054完了後まで実装しない。

## 18. 用語

| 用語 | 定義 |
| --- | --- |
| NvencCaptureProcessState | Phase 0.11 Production Composition Rootがprocessにつき1個だけ生成し、Backend／Run Coordinator／Publication Serviceへ同一参照を注入する非永続のfail-stop authority。`Running / Draining / PoisonedUntilProcessRestart`だけを持ち、Poison後は解除、再生成、同process Recoveryを許さない。Tier AだけはFixtureごとに独立したtest instanceを使う |
| NvencEncodeSampleSlot | Phase 0.11の1件のin-flight encodeへ排他的に束縛するCapture専用固定資源。Run開始前に1回だけ生成・登録しRun中不変とするNV12 Input Texture／native pointer／registered handle、mapped所有状態、Output Bitstream Buffer、Completion Event／async-event登録、GPU変換完了証拠を論理的に対応付ける。Work Slotとは別Pool／別lifecycleで固定8組を持ち、reorder bufferには使わず、各Frameで生成・破棄・再登録しない |
| NvencOwnedEncodedAccessUnit | Phase 0.11 Output Worker内部でOutput CollectorからRun Chunk Sinkへ一方向に移すprocess-localな非copy／一回消費Lease。Work Token、CaptureFrameId、Run開始時に1件だけ確保した16 MiB固定owned byte領域、valid length、領域返却authorityだけを持ち、永続Schema、Artifact／Registry／Session／Freeze authority、file path、H.264解析、offset／PTS／DTSを持たない |
| NvencRunChunkSink | Phase 0.11でOutput Worker上から同期呼出しされ、owned Access Unitのaccepted FIFO append、checked ByteLength、streaming hash、Frame Relation、owned領域の厳密に1回の返却と同期結果だけを担当する内部責務。NVENC資源、Frame Completion、Worker／Queue／Drain／Join、Publication／Recoveryを所有しない |
| VP Geometry | グローバルVertex／Indexプール上の表示／Stencil共用表現。VertexはAoS。CPU側が切断正本、GPU側が描画コピー。寿命・公開は4.5に従う |
| Geometry参照 | 正負Sideの共用Geometry範囲への参照。幾何処理が必要とする局所Component識別を保持できるが、通常物理所有単位をComponent数で増やさない |
| Stable Geometry | 4.5.6の必要転送と現在有効なframe／参照での表示Commitを満たす共用VP Geometry。Final cook・物理Commit完了を含意しない |
| Pending Cut | 実命中で受付済みで、必要なGeometry／Physics工程が未完了の切断記録。受付時にCutOperationId、親LogicalFragment、更新前の基底世代、採用面を持ってObjectGeneration更新と原子的に公開する。確定子・実在境界の構築Validator合格前はLogicalCutOperation、CutBoundaryRecordを持たず、即時表示はこの採用面と4.2の表示可否を使う。この間は置換予定の親と仮表示領域への後続切断を受付けない。Operation公開後も必要工程が正常完了または既存規則で終端するまで受付情報を維持するが、公開済み子の再切断を妨げず、Geometry Commit後はTemporary描画対象から外れても未完了の有効な物理工程のCommit照合に使う。Operation未公開のまま既存規則で終端失敗した場合だけ当該記録と仮表示を退役する |
| TemporaryRenderCapRecordSet | 当該フレームに実際のStencil／Cap Batchへ投入するRecord集合。4.2の表示可能なPending CutとGeometry未Commit境界から構成し、固定状態で省略しない。既存Frustum／Facing Cullとclip上限を適用し、実Cap 2～4枚上限へ数え、Geometry Commit後は対応Recordを外す |
| TemporaryClipConstraintCandidateSet | 1個のRenderFragmentへ関係するGeometry未Commit切断半空間制約の集合。Pending Cutと公開済み境界の採用面・祖先制約から構成し、固定状態で省略しない。Cap Record集合とは区別する |
| SelectedTemporaryClipPlaneSet | CandidateをPending Cut列とCutBoundary Record公開列の受付順に辿ったdependency-closed prefixから、Raster 8面とPixel fallback最大4面へ割り当て、左右眼とColor／Depth／Shadow／Stencil Volumeで共有する固定長の即時描画Plane集合。Operation公開時は同じ受付位置と面を維持する |
| IgnoredTemporaryClipBoundarySet | Candidateのうちdependency-closedな最大12面prefixへ入らない後発Pending Cut／境界。即時Rendererの各Passと対応Stencil Volume submitだけから除外するが、Pending CutまたはCap Record、公開済みCutBoundaryRecord、論理／物理状態、世代、背景Geometry／Convex処理には残すbounded degradation集合 |
| 共用Cut Geometry | 表示、実Cap、Stencil Volume、次回切断が参照する唯一の切断Geometry。閉鎖・edge／vertex manifold・局所winding整合済みTopologyを持ち、各世代で同じTriangle集合と向きを全用途へ公開する。Self-intersection、別Topologyの閉Component間のIntersection／Overlap、Internal／Nested／Coincident、全体反転、Runtimeの面積0 Triangleを許容する |
| ClosedCutComponentSet | 幾何処理またはAsset表現が必要とする、独立に閉鎖・切断・CapできるComponent範囲。Component間のIntersection／Overlapを許容する。通常切断のための全島列挙、物理連結成分Graph、Boolean Unionは要求しない |
| ComponentFragment | 幾何処理が局所的に必要とするClosed Cut Componentの切断片。同Sideに複数存在できるが、全島列挙や個別の論理子・物理所有者を要求しない |
| Physics Proxy | 物理接触と高速切断のための低複雑度Convex／Compound。各Convexは閉凸契約を満たすが、同一Compound内の別Convex同士はOverlapしてよく、Strict SolidやConvex Boolean Unionを入力に要求しない |
| PhysicsConvexMassWeight | 同一FragmentGroup内の各Physics Convexへ親質量の配分比を与えるbinary64、finite、0以上のFinal用Metadata。Local ID順の左畳み和がfiniteかつ正であることを要求し、`assignedMass = parentMass * (weight / weightSum)`の固定順で配分する。Convexの生体積とは独立して重複Compoundの二重計上を避け、非交差時は継承し、交差時は当該Convexの子体積比だけで分割する。切断後はFinal物理Commit対象Fragmentごとにも和がfiniteかつ正でなければならない。Weight 0 Convexだけの子を空集合へ読み替えず、現物理を正式採用してGeometryをその構成へ追従させる |
| FragmentGroup | 同じ切断系譜、Canonical Mass Budget、点Anchor集約、世代、Final物理Commitを共有するLogical Fragment集合。Provisional成功時は複数Actorを持て、分裂を成立させられない場合は利用可能な既存物理表現を正式採用した`Stable Unsplit`で終端できる |
| PendingPhysicsSplit | 評価または必要資源待ちで、見た目と論理状態は切断済みだが、1つのRigidbody／旧Colliderを共有して物理分裂を待つ状態。成立不能が確定した後の終端状態には使わない |
| ProvisionalRigidbody | Final Convex cook前にLogical Fragment別のpose／速度／外界Collisionを持たせる短命Actor。旧cook済みConvex Geometryを共有し、OBB切断体積比または等WeightでCanonical Mass Budgetを保存した近似mass／COM／inertiaを持つが、Final質量特性の正本にはしない |
| ProvisionalSeparationConstraint | 同じ切断で生じたProvisional Siblingの相対回転と接線移動を抑え、切断面法線方向の分離だけを許可して初期位置より深い再侵入を防ぐ短命Constraint。Sibling Collision無効化とは別の役割を持つ |
| ProvisionalCollisionResourceLease | 1つのcook済みConvex Geometryを複数のProvisional Shape Instanceが安全に共有する所有権Token。Actor公開前に取得し、Shape除去と物理ステップ完了後に一度だけ返し、最後のLease前にGeometryを破棄しない |
| ProvisionalLastFinitePhysicsSnapshot | 公開済みProvisional Groupごとに2個の固定Slotを持ち、HeaderのObjectId／ObjectGeneration／FixedStepId／ActorCountと、LogicalFragmentLocalId順の全Actor pose／速度を同一Fixed Stepからall-or-noneで公開する物理安全Snapshot。Staging完了後のatomic Slot切替だけを読取可能点とし、`ProvisionalFaultFrozen`への封じ込め専用で表示―物理誤差の測定、補間、すり合わせには使用しない |
| CanonicalMassBudget | Provisional Actorの近似mass特性と分離して保持する、切断直前の正規親質量とPhysicsConvexMassWeight系譜。Provisional OBB配分とFinal mass／COM／inertia計算の親Budgetとして参照し、連続切断でもSolver用Actor massから作り直さない |
| FragmentRenderAnchor | Parent ActorからProvisional Actorを初めて分裂させる際、表示Fragmentの初期World poseと点速度を連続させるstableな基準Transform。Final handoffでは物理Actorを優先するためActor pose補正やCOM速度変換には使用せず、表示Geometryが新しい物理frameへ追従して瞬間移動することを許容する |
| FragmentGroupPhysicsState | 固定値`Invalid=0`、`StableUnsplit=1`、`PendingPhysicsSplit=2`、`PendingAnchoredSplit=4`、`ProvisionalPhysicsSplit=5`、`ProvisionalAnchoredSplit=6`、`StableFastCook=7`、`PhysicsUpgradePending=8`、`StableFastSimulation=9`、`ProvisionalFaultFrozen=10`。未知値を公開せず、TraceのFromState／ToStateへ同じ値を使用する |
| ProvisionalPhysicsFallbackReason | Provisional公開前の構築失敗専用。固定値`None=0`、`ActorCapacityExceeded=1`、`ShapeCapacityExceeded=2`、`ConstraintCapacityExceeded=3`、`GeometryShareUnsupported=4`、`ShapeClassificationInvalid=5`、`MassApproximationInvalid=6`、`ActorCreationFailed=7`、`ConstraintCreationFailed=8`、`GenerationMismatch=9`、`AtomicCommitFailed=10`。Fallback EventではNoneを禁止し、公開後異常へ流用しない |
| ProvisionalRuntimeFaultReason | 公開後FaultのPrimary原因専用。固定値`None=0`、`NonFiniteActorState=1`、`ConstraintRuntimeFailed=2`、`LinearVelocityLimitExceeded=3`、`AngularVelocityLimitExceeded=4`。複数原因はこの順を優先し、同順位は最小LogicalFragmentLocalIdを選ぶ。Safety Frozen EventではNoneを禁止する |
| ProvisionalFaultContainmentDisposition | Primary Fault後の封じ込め結果。固定値`Invalid=0`、`RestoredAtomicGroupSnapshotAndFrozen=1`、`RemovedFromPhysicsSceneSnapshotUnavailable=2`、`RemovedFromPhysicsSceneContainmentValidationFailed=3`。Faultを検出した現Step値は使用しない。Safety Frozen EventではInvalidを禁止し、Primary Fault Reasonと独立に記録する |
| FixedSupportAnchor | 固定支持を表すfiniteな点。初期Logical Convex CellとFragment Physics Frame内local位置を入力とし、各Cellの集合で保持する。OnPlaneでは同じ値を正負の各子集合へ1回ずつ継承する。所有単位内に一つでもあればStatic／Kinematicで固定とし、他Cellへ支持を伝播しない |
| LargeStructuralProp | 外周Structural Slabと少数Compound Convexで近似する大型プロップ。点Anchorを失えば通常の動的物理へ進み、落下・横倒し・完全倒壊を許容する |
| IsBuildingDerived | 製品Recipeが指定する建物由来の継承boolean。サイズやSlab数からRuntimeで推定しない。7.2.2を正本とする |
| BuildingSplitDepth | 7.2.2の建物由来の分裂深さ。初期0、建物由来のProvisionalを含む1→2物理公開時だけ両子を一度更新し、非建物の系譜は0を維持する。Final採否で再加算・巻戻ししない |
| BuildingWorldD6Constraint | 7.2.2の建物由来・Anchorなしの動的分裂子が一つ持つWorld接続D6。Actor寿命で保持し、ProvisionalSeparationConstraintと分離する |
| StructuralSlabComponent | 建物外周等の厚い壁板Component。装飾付き共用Cut Geometryと原則1個、入口等では少数の直方体Physics Convexを対応させ、必要に応じて下端等に点Ground Anchorを持つ |
| PlayerLocomotionOccupancy | PlayerとプロップのPhysX接触を使わず、大型固定／構造プロップとレベル境界のOBB／Box／Capsule等についてPlayer Root／予測HMD Capsuleの人工移動可能域を近似する低複雑度Volume集合。全Render MeshのCamera包含や非干渉物体の視点被りを判定・防止するものではなく、押し戻し対一方向退出のPolicyとも分離する |
| OccupancyVolumeLocalId | 0を未設定用に予約し、1つのLevel実行期間中にPlayerLocomotionOccupancyのPrimitiveへ一意かつ非再利用で割り当てる正の32bit int。有限退出候補の生成順、ExitMetricのDepth Vector順、同値判定を決定論的にするために使用する |
| PlayerLocomotionPolicy | 非接触Locomotionが禁止領域との重なりを扱う固定方針。`NewEntryReject=1`、`PushOut=2`、`ExitOnly=3`とし、0は未設定、未知値はRejectする。PoCは`NewEntryReject`を正本とし、`PushOut`対`ExitOnly`はプレイテスト後に決める |
| ForcedOccupancyOverlap | Player自身の操作ではなく移動するOccupancyが現在姿勢へ侵入した一時状態。物理CommitやHMD姿勢を巻き戻さず、Profile上限の固定長作業領域でAllowedLocomotionPlane上の有限候補を全関連Volumeについて評価し、決定論的ExitMetricが厳密減少する人工移動だけを量の下限なしで適用する。全侵入深度が`occupancyExitEpsilon`以下になれば通常Policyへ戻り、Episode期限内に戻らなければfail-closedする |
| OccupancyExitBlocked | ForcedOccupancyOverlapで容量、探索範囲、減少候補またはEpisode期限の契約を満たせないfail-closed状態。人工並進と物理的押し出しを止めてFadeを維持し、明示的な安全Pose復帰、Level Reset、またはOccupancy変化だけで再開する。`OccupancyExitBlockReason`を保持する |
| OccupancyExitBlockReason | 固定値`None=0`、`NoDecreasingCandidate=1`、`SearchBoundsExceeded=2`、`VolumeCapacityExceeded=3`、`CandidateCapacityExceeded=4`、`EpisodeTimeout=5`、`NonFiniteDepth=6`。0と未知値でBlocked状態を公開せず、容量超過を部分評価成功へ読み替えない |
| PendingAnchoredSplit | 点Anchor配分から所有者単位の固定／動的は定まったがCollider切断／Bakeは未完了で、旧Colliderを固定したまま自由側だけを衝突なしで仮表示する状態 |
| LogicalFragment | 蓄積された切断面で区切られた論理単位。通常切断では正負各最大1個とし、同Sideの離れた島を含められる。Colliderや表示完成前に公開でき、後続切断の対象になる。7.9の再編成・退役で現在の所有者対応を更新しても、過去履歴を架空Operationで補わない |
| CutBoundaryRecord | 切断面、Side、確定子参照、Geometry状態、作成時世代を保持する実在の幾何境界記録。物理連結性を表すEdgeではなく、正負Geometryが同じ物理所有者へ所属しても履歴を保持する |
| SourceSlashPlane／SelectedObjectLocalCutPlane | 前者はSlashFrameの不変World攻撃面、後者は命中時に対象操作ごとに一度確定する切断前物体相対の面。19.5.1の予測面採否はMesh Readyに依存せず、全表示／物理成果物が選択面を共有する |
| CommittedCutPlane | SelectedObjectLocalCutPlaneを対応する実Actor／子frameへ写したWorld面。命中時のWorld位置を固定したり予測PoseをActorへ設定する指示ではない |
| RigidCutRebaseProfileV1 | 初期自由飛行剛体のリベースGateを固定するversion付き設定。法線角度、Bounds内plane field差、固定8点の左右眼投影差の上限を持ち、未設定や投影不能では通常面へFallbackする |
| LogicalCutOperation | 一つの親への受付済み切断の確定論理操作。CutOperationId、親ID／世代、正負各最大1個で合計1～2個の非空直接子ID、実在する0～256個のCutBoundaryIdを持つ。子はGeometryまたはConvexが非空のSideとし、存在しない子や境界を補わない。既存ID・世代・参照のValidator合格後にFragment／Boundaryと原子的に一度だけ公開する。受付前の片側空No-opは作らない |
| CutOperationId | 0を未設定用に予約し、ObjectIdの生存期間全体で一意かつ非再利用とする正の32bit int。受付成功時にPending Cutへ発行するため、対応LogicalCutOperationより先に存在できる。Operation系TraceではValue0へ格納する |
| LogicalFragmentLocalId | 0を未設定用に予約し、ObjectIdの生存期間全体で一意かつ非再利用とする正の32bit int |
| CutBoundaryLocalId | 0を未設定用に予約し、ObjectIdの生存期間全体で一意かつ非再利用とする正の32bit int |
| Kerf | 切断によって除去される物理的な幅。本作では0とし、見える隙間は破片の相対移動だけで生じる |
| Cooking Profile | `Physics.BakeMesh`と`MeshCollider`へ同一指定するcookingOptionsの構成。初回分裂用Fast Cookと選択的Upgrade用Fast Simulationを使い分ける |
| Physics Upgrade | Stable Fast Cook破片と同じ形状の別MeshをFast Simulationで再Bakeし、安全な物理ステップ境界でColliderを昇格させる処理 |
| RenderFragment | 正負Sideの共用Geometryを表示する単位。同Sideの離れた島を含められ、全島の個別列挙を要求しない。7.6に従い現在の物理所有者へ追従する |
| LogicalConvexFragment | 自前Convex切断で生成されるcook前の論理物理成分。まだUnity Colliderとして適用済みとは限らない |
| LogicalConvexFragmentLocalId | ObjectId＋ObjectGeneration内だけで一意かつ非再利用とする正のintのLogicalConvexFragment識別子。0は未設定用に予約し、TaskIdとは独立 |
| 共用Geometry物理所属 | 7.6のSide別Convex集合の正常な空／非空により、正負Geometryを現在有効な物理所有者へ対応させる関係。反対側へ付いても面・Side・Capを維持し、GeometryごとのConvex選択・Coverage・専用Convex・質量移送を要求しない |
| コミット後の追加空間分割 | 確定した1物理所有単位の共用面集合を横切らない一枚の平面で配分し、必要なConvex clip・cook後に通常物体2個へ再編成する7.9の任意処理。非命中で公開し、新しいGameplay切断面・実境界・Cut Operationを作らない |
| 物理GC | 共用Geometry全体の面集合が確定0の通常物理所有単位を、7.9の条件で丸ごと終了する任意のゲーム上の寿命Policy。Managed GC・個別Convex間引きではなく、追加分割から独立する |
| MaxIncompleteCutOperationCount | 全対象で同時に保持できる受付済み未完了切断数。対象LogicalFragmentへの一回の受付を1件とする既存容量設定の0より大きい固定値であり、片側空No-op、受付見送り、投機候補、任意Upgrade、7.9の任意分割・物理GCは数えない |
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
| WorldPhysicsProfile | 世界重力を正本として保持し、Unity Physics、予測、解析運動、VFXへ同じ値を供給するバージョン付き設定 |
| Pending Two-Sided Shadow | 即時切断中だけ、開いた外殻の裏面をShadow Mapへ書いて断面キャップの遮蔽を近似する両面ShadowCaster経路 |
| Cap Bounds Polygon | 対象のローカルOBBと切断平面の交差から生成し、他のTemporary Render Boundary半空間でclipする3～6頂点の有限な仮キャップ板 |
| Stencil Conflict Graph | 通常Colorで分離が必要な対象間の関係を表す論理モデル。全Graph、全Edge、特定の構築・彩色手順を要求しない |
| CapCompatibilityKey | 全World Cut Plane、Side／半空間、分離Offset、Cap Material／Debug／Fade状態を表すStencil共有互換Key。符号分類とWinding容量を含めない |
| Winding Count Stencil | 共用Cut GeometryのFront／Backで排他的に予約したStencil Byte全8bitを128へ初期化してIncrementWrap／DecrementWrapし、`S=(128+W) mod 256`のうち`S>128`だけを描画する方式。Saturateおよび部分Bit Counterは使用しない |
| Residual Stencil Support | Front／Back集計後に`S != 128`となる画面領域を表す論理概念。実Stencilから検出せず、可視Cap Boundsを通常Colorの保守的な投影重複判定に使う |
| Cap Visibility Cull | 論理破片×切断面のCapRecordを左右眼で判定し、全Capが両眼とも裏向きの互換GroupをStencil彩色前に除外する処理 |
| SlashGeneration | GestureをLatchするたびに進む、斬撃入力単位の単調増加番号 |
| ObjectGeneration | 対象への実命中とPending Cut登録時に進む単調増加番号。Operation公開前の終端失敗でも巻き戻さず、後着成果物のCommitには世代一致に加えて対応する有効なPending CutとCutOperationIdの存在を要求する |
| BaseObjectGeneration | 投機ジョブが入力としてスナップショットしたObjectGeneration |
| Commit | 検証済み成果物を描画・物理状態へ原子的に差し替える操作。切断成果物では世代一致に加えて、対応する有効なPending Cutと`CutOperationId`の一致を要求する |
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
| Temporary Physics Proxy | Final Colliderが未完成の間に使う簡易ConvexまたはCompound Primitive。表示Geometryを含まず、正しさをT-077、生成費用をT-076で測る |
| Geometry／Cook Microbenchmark | CPU側の共用VP Geometry切断、Convex切断、Temporary Physics Proxy、cookを固定Datasetで工程別に測り、計算KernelのSingle-Thread µs/op、Bake／Commit単発Latency、Job Batch Throughput／End-to-End latencyから容量式を作る性能検証 |
| GeometryBenchmarkRunManifest | Cook ProbeとGeometry／Cook Microbenchmark専用のversion付きcanonical JSON。1 Manifestは単一DatasetCaseIdの固定規模軸と、単一Target／Stage／ExecutionMode／CookingProfile／Metric／Unitの1測定系列を表し、BenchmarkSuiteIdで複数系列を束ねる。同一SuiteではDatasetIdからDatasetContentSha256への写像を一意にする。Target×Stage×Mode、全propertyの型・値域・null条件・順序を固定し、clean Repositoryだけ保存を許可して既存TraceRunManifestを拡張しない。v1のLoader上限は64 KiB |
| DatasetCaseId | DatasetContentSha256で固定されたDataset内の1入力caseを識別するID。早期Licensed Fixtureではv2 Index内の正確なFixtureBindingへ一意対応させ、同じGeometryでも異なる用途を混同しない。SourceFixtureIdは最大64文字、DatasetCaseId全体は既存Manifestの`[A-Za-z0-9._-]{1,128}`内で一意であることを検証する。旧SourceFixtureId.TierToken.VariantId構築式を復活させない。Syntheticは別Dataset IDと固定Case IDを使う。同一SuiteのDatasetId→hash写像と各caseの規模軸は不変とする |
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
| Early Licensed Fixture | Phase 0.2の事前Eligible Sourceからカテゴリ別Presetで得た非公開Geometry。形状と用途をFixture Bindingで分離し、最低quotaと完了batch frontierをReceiptへ固定する。製品変換済みAssetや全Asset互換の証拠ではない |
| Synthetic Watertight Test Fixture | プログラムまたは固定版Blenderスクリプトから決定論的に生成する閉Triangle Meshのテスト／Benchmark専用入力。製品AssetやライセンスAssetの派生物ではなく、製品Preprocessor成果物、Runtime同梱物、代表Asset合格条件にはしない |
| SyntheticWatertightFixtureProfile | Synthetic Watertight Fixtureだけに適用するepsilon、Triangle／Component／自己交差候補上限と共有Validator algorithmを固定したcanonical Profile。EarlyFixtureSelectionProfileとは別hashを持つ |
| SyntheticWatertightDatasetIndex | Generator Recipe、Synthetic ZCG、合格Validation ResultのhashをCaseごとに固定するcanonical Index。Licensed Source／Tier／Reportを参照しない |
| SyntheticFixtureValidationResult | Synthetic ZCGの閉Topology、向き、成分volume、自己交差と上限を共有Validatorで検査したcanonical結果。不合格をLicensed ProcessStatusへ変換しない |
| LicensedRepresentative Dataset | Early Licensed Fixtureを同じHarnessで測る非公開の補助Dataset。公開合成Fixtureのcanonical結果が実Asset傾向から大きく外れないか確認するために使い、入力GeometryとAsset対応は公開しない |
| EarlyFixtureSelectionProfile | schema v2。5カテゴリ、Recipe許可表、Geometry／Role／Selection Gate、固定順位、監査toleranceを固定するcanonical Profile。割当は独立EarlyFixtureBatchAllocationProfile v1、実行timeout／memory制御はEarlyFixtureResourceProfile v1へ分離し、生成identityと再評価identityを区別する。変更で全旧成果物を一律無効化しない |
| EarlyFixtureBatchAllocationProfile | schema v1。カテゴリ投入数、7 queueと配分、Source順、不足時非消費停止、Source単位のVariant所属だけを固定する独立canonical文書。Catalog／ManifestのBatchAllocationProfileContentSha256の唯一の参照先 |
| EarlyFixtureResourceProfile | schema v1。BatchKind、120秒／単一300秒resource retry、Blender非並列、Pilot／Expansion共通32 GiBの単一Blender Working Set停止閾値と通常1秒の監視周期を固定する。使用hashをAttempt／生成Entryへ記録しSource割当と分離する |
| EarlyFixtureSourceCatalog | schema v2。固定property順でvendor入力・CatalogEntryOrdinal・Source identity・Category／stratum・Eligible／Excluded・Entry Rule／ScopeReasonを保持する。Source indexed root外へ置き、rootのSourceBundleContentSha256で正確なSource Index、Rule Set ID／hashと割当Profile hashを固定し、全Batch Manifestの一括確定後にFreezeする |
| EarlyFixtureBatchManifest | schema v1。Catalog Freeze時に全均衡batchを一括確定する割当正本。BatchId／BatchOrdinal／Kind／Catalog・割当Profile hash、Source列のBatchSourceOrdinal／Category／stratumを持つ。自身のhashは外部参照／Receiptに置き、結果による再割当をしない |
| CanonicalBundleIndex | Source／Script／Presetの展開済み通常fileを正規化相対path、byte長、raw content hashで列挙するversion付きcanonical Index。空directoryやtimestampを無視し、symlink等を拒否する。Index bytesのSHA-256を各Bundle Content SHA-256とし、Verifierが実rootの欠落／余分file、長さ、hashをBlender前とReceipt前に完全照合する |
| ZantetsuCanonicalGeometry | Phase 0.2のLicensed Render Triangle Mesh、Synthetic Watertight Triangle Mesh、またはConvex Setを、meter／Y-up／左手系、正規化binary32位置、決定的なposition／face／hull順で保存するversion付きcanonical binary。v1は切断／Cook Benchmark用の形状Topologyだけを持ち、拡張子は`.zcg`、decode後の再serialize一致を必須とする |
| SolidSignedVolumeV1 | Synthetic Watertight ZCGの連結成分について、成分Bounds中心、canonical Triangle／成分順、triangleごとの除算、binary64左畳みを固定して正体積を判定するテスト専用volume契約。Licensed／製品Solidを意味しない |
| SolidGeometryValidatorV1 | Synthetic Watertight ZCGだけを読み、閉Topology、`SolidSignedVolumeV1`、`ClosedTriangleDistanceV1`を同一artifactで検証するversion固定Validator。Synthetic Script Bundleで内容を固定し、Licensed Harnessと製品Preprocessorから呼び出さない |
| SolidCandidateBvhV1 | Synthetic Watertight Triangleのepsilon拡張AABBから固定axis／median規則で構築し、自己交差の一意候補pairだけを生成するテスト専用の決定論的BVH。候補はcanonical順へsortし、Synthetic Profile上限で停止する。Licensed／製品Meshへは実行しない |
| ClosedTriangleDistanceV1 | Synthetic Solid自己交差Validatorの非共有Triangle pair用距離predicate。固定順のpoint-to-closed-triangle／segment-to-closed-triangle／closed-segment距離候補と`epsDistance`で分類する。共有indexが1または2のpairは`SharedSimplexIntersectionV1`で共有simplex外の実交差を検査し、epsilon近接だけでは拒否しない |
| Early Fixture Reduction Variant | 許可カテゴリ／Recipeから1回Ratioで生成する要求Tri Target別の派生Geometry。Actualが規模正本。NoOpは直接入力、Aliasは同じSource／Kind／hashの最初の先行Artifactを参照する。Characterには生成しない |
| Early Fixture Voxel Variant | Phase 0.2 VehicleのVoxelSolidify1cm基底と限定Post-Decimate。meter基準0.01 m、Separate per Source Objectを固定し、相対Voxel64／128／256を使わない。Licensed Solid検証を要求しない |
| EarlyFixtureSelectionReport | schema v2。完了frontier内の全PlannedVariantEntry、Attempts、ProcessStatus、成功時のtagged出力とGenerated／NoOp／Alias、失敗Reason、Entry単位provenanceを記録する非公開Report。時間／memory観測値はDataset hash外 |
| LicensedRepresentativeDatasetIndex | schema v2。用途別Binding、Geometry／sidecar／支持用親closure、各path／length／hash／provenanceを固定する非公開Index。異cohortを収録できるが順位比較は分離し、canonical bytesのhashをDatasetContentSha256とする |
| LicensedFixtureSelectionReceipt | schema v2。Report／Index／Dataset、Catalog、LastIncludedBatchOrdinal、0..LastIncludedBatchOrdinalの完全prefixのBatch Manifest外部hash参照列、採用実数を結ぶ最終commit marker。既存Receiptへ追記せず、新revisionでも検証済みArtifactを再利用できる |
| PlannedVariantEntry | Source／Variant／Recipeの予定処理record。ProcessStatusの終端5値を持ち、Succeededだけが出力tag／ID／Result Kindを持つ。Geometryの選抜状態とは別 |
| Geometry Artifact／FixtureBinding | Geometry ArtifactはKind／Recipe／親／provenance／ZCG identity、Bindingはその成功Artifactまたはsidecarと1個のRoleを結ぶ。複数用途でもbytesを複製しない |
| StructuralSlabFixture | 成功Building Geometryへの薄いsidecar。Slabの元Component、building-local Transform、平面／normal、OBB、canonical外周順を持ち、Selectedには4枚以上を要求する。Ground Anchorは持たない |
| PhysicsCookInput | Phase 0.25の予定Cook入力Role。Phase 0.2は単一Hull・最大128頂点、閉凸多面体の導出polygon上限252を適用する。Codec255頂点および後段Runtime制限と区別し、Cook成功自体は保証しない |
| Selection frontier／cohort | frontierは人間がFreezeした連続完了batchの末尾。cohortは同じ生成・評価条件の比較集合。新revisionは全件再生成を意味せず、旧Artifactの生成provenanceを保持する |
| Preprocess Recipe | Assetごとの包含・除外部品、封鎖、空洞保持、分割、Voxel品質を記述する設定 |
| Preprocess Cache Key | 入力、Recipe、Script、Blender版のハッシュから生成する再構築判定値 |
| Boundary Loop | 片面または開放Meshで、1面だけに属するEdgeが形成する穴の輪郭 |
| BoundaryLoopFill | Phase 0.2の本命簡易封鎖Variant。Object／Topology Component内で面が1枚だけ接続するBoundary Edgeを抽出し、全頂点次数2かつProfile内の閉Loopだけをstable順に個別Fill／三角形化する。分岐やOpen Chainを推測修復しない |
| BlindNonManifoldFill | Poly Pro Universeの人力調査で有効性を確認したBlenderのNon-Manifold選択＋`F`操作を固定Presetで再現するPhase 0.2探索Variant。無条件採用せず、Hard形状偏差とZCG後Gateを通過した結果もBenchmarkOnlyに限定する |
| Voxel Closing | 体積を膨張後に収縮してVoxel数個以下の隙間を閉じる形態学的処理 |
| RenderCutTopologyMap | 共用Cut Geometryのposed positionとは独立して維持するTopology系譜。TopologyVertexId、OriginalEdgeId、TriangleInstanceId、EdgeUseIdを持ち、attribute seamを同じTopology Vertex／Original Edgeへ対応付ける |
| ContourPortKey | Cap Contourのnodeを空間座標ではなくOriginal Edge、またはTopology VertexとLocal Portで識別するKey。同一点にある別Topology由来のportを統合しない |
| RenderCutRobustnessProfile | Distance／Length epsilonとContour／Triangle／byte／時間上限を固定する共用Geometry切断設定。面積0除去、許可Fallback、Stencil専用設定、個別Physics Convex設定を含まない |
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

攻撃全体の不変面を`SourceSlashPlane`と呼ぶ。19.5.1で剛体ごとの操作Local Planeを採用しても、このSource面、SlashFrame、SlashFront、有限Sweepと命中履歴は変更しない。以下のLatch面はSourceSlashPlaneを指す。

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

切断面はLatch時点で不変になるため、最終的なSlashFront形状が未確定でも、切断面の厚み、最大飛距離、設計上許容する最大前縁範囲から`Candidate Flight Bounds`を作り、遠距離候補をBroadphase列挙できる。これは投機計算用の保守的Boundsであり、当たり判定には使用しない。候補への表示／Stencil共用VP Geometry／Convex切断はLatchを依存条件として投機開始し、実際のFrontEdge Sweepによる命中だけをCommit Gateの条件とする。

```text
刀軌道Sample
  -> Slash Latch
       ├-> 初期SlashFront生成・飛翔・重なり検査 -> 近距離即時表示
       ├-> Candidate Flight Bounds候補列挙 -> 投機Mesh／Convex切断
       └-> Extending中も既存前縁を前進 + 頂点／辺追加
                              └-> FrontEdge Sweep実命中 -> Commit Gate
       -> Finalizedで形状追加終了 -> 完成前縁は飛翔継続
```

候補へ列挙されてもSlashFrontが実際に命中しなければ成果物を破棄する。実接触時に`FrontEdgeId`、対象位置、回転、Animation状態、対象世代を検証する。19.5.1の対象剛体では、姿勢差そのものではなく実姿勢でのLocal Plane採用Gateを用いる。面の採用と成果物Readyを分離し、採用面は未完成でもTemporary／Provisionalへ使い同じ面の完成を待つ。対象外は従来の姿勢検証を維持し、不採用なら実姿勢から求めた面で後追い計算する。

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

メインスレッドはUnity状態を数値データへスナップショットし、Job SystemとBurstは予測、頂点分類、交差、断面生成を行う。UnityのGameObject、Transform、Animatorをワーカージョブから直接操作しない。完成VPは4.5.6の所属・Index仕上げ依存を経てGPU更新・Geometry参照公開へ進み、物理適用は表示仕上げを待たず独立に進む。Commit Controllerがそれぞれ描画フレーム／物理ステップ境界で適用する。

### 19.3 未来姿勢の求め方

表示切断の先行計算は4.5.2に従い、Skinned対象の先行計算はJobベイク導入採用時に不変Rig Poseから共通VP入力を生成して共用Geometry切断へ接続する。同じRig Poseから骨Physics Proxyを姿勢化し、表示頂点ベイクだけの待ちを物理へ追加しない。準備中は現在Sceneと表示を維持し、4.5.6の正負直接Index出力・転送まで準備して、19.5の既存採用条件を満たすVP・成果物を命中時に再利用する。Final物理所属の確定を転送条件にせず、非スキニングまたは有効な準備済み入力では不要な工程を省く。

| 対象状態 | 予測方法 |
| --- | --- |
| 静止 | 現在姿勢を採用 |
| 自由飛行・単純重力 | `DirectRigidPredictionEligibilityGate`を満たす剛体は、固定Unity／PhysX版の固定刻みに準拠したO(1)直接予測を使用する。Gate対象外はPhase 4.5では後追い処理、Phase 4.6以降は適用可能な場合だけ局所Prediction Physicsへ送る。剛体成果物の実姿勢リベースは19.5.1の独立した採用Gateを使う |
| 既知またはMobPlanで確定したAnimation | 対象`FixedStepId`の副作用のない`ExplicitAnimationState`を解決し、交換可能なPose Evaluatorで任意時刻Poseを生成 |
| 接触・転動 | Phase 4.5では後追い処理。Phase 4.6以降は適用可能な対象だけ局所Prediction Physics |
| ユーザー／スクリプト依存 | 入力を複製できる範囲だけ投機評価 |

`DirectRigidPredictionEligibilityGate`は開始Snapshotだけから判定する内部boolean受付条件とする。動的かつSleep中でなく、`useGravity=true`、damping 0、Rigidbody Constraintsなし、既知のシステム所有Constraintなし、Fixed Step境界、`WorldPhysicsProfile`一致、既知Contactなし、予約済みForce／Torque・スクリプト駆動・Animation駆動・ユーザー介入なしであり、予測区間に速度Clampが適用されず、既存候補情報から衝突可能性が判明していない場合だけ受理する。未来区間の完全な無衝突証明は要求せず、受付後に衝突または介入が判明した成果物は既存の実命中検証で破棄する。Gate結果用の状態、enum、Profile、Proof、永続ArtifactまたはTrace Eventは追加しない。

一般外部Jointは7.2.2の製品入力契約で除外するため、Gateで任意Jointを列挙しない。点Anchorで固定された対象、接触・転動中の対象と、既知のBuildingWorldD6Constraint／ProvisionalSeparationConstraint付き対象は直接予測から外す。いずれかのConstraintを持つ対象の局所予測も初期範囲に含めず、既存の後追い処理で成立させる。

Current／Future Animationの意味上の正本は、ゲーム側が保持する副作用のない`ExplicitAnimationState`とGlobal `FixedStepId`である。`Animator`、`AnimatorController`、`AnimatorControllerPlayable`の内部State、Clock、Trigger、Transition、BlendをAnimation Planへ読み戻さず、稼働中Controllerを未来へ進めたり巻き戻したりしない。Future Pose `T+n`を得るためにControllerを`T+1 ... T+n-1`へ逐次rolloutする方式を標準経路にしない。

`FutureAnimationPoseEvaluator`は対象`FixedStepId`、そのStepへ解決済みのimmutableな`ExplicitAnimationState`、対象Rig／Animation Asset Set／Evaluation ProfileのIdentityを一体化した`ResolvedAnimationPoseInput`を受け、canonical Bone順のRig Pose Bufferを出力する。裸のState、別Stepへ属するState、Catalog Identity不一致を受理せず、Evaluator自身は`PlaybackRate`や現在時刻からPhaseを追加進行しない。入力Stateと現在Sceneを変更せず、同じ入力とBackendでは要求順に依存しない同じPoseを生成する。内部Cacheや前処理は許可するが、任意時刻評価に必要な履歴をEvaluatorの隠れた可変状態へだけ保持しない。履歴依存方式を後から導入する場合は、Transition元、Source Time、Blend、Foot／Inertialization履歴要約等を明示StateまたはPlanへ含める。

共通Pose Evaluatorは意味境界であり、全BackendをBurst Jobから呼べるとは仮定しない。Pose Table／custom samplerは固定長Bufferを使うJob Batch候補、Unity Playable／Animator出力はpool済みGraphを使うMain Thread予算対象としてSchedulerがBackend別にRoutingする。Playable評価をWorkerへ偽装したり、候補ごとのGameObject／Graph生成、Main Threadの無制限Evaluateを行わない。

AnimatorコンポーネントはHumanoid Retargeting、Avatar Binding、Playable出力先、現在表示のための任意Backendとして使用できるが、上位Stateのauthorityではない。標準V1はゲーム側Stateからcontrollerなしの`AnimationClipPlayable`／Mixer、または事前Bake済みPose Tableへ明示Source Time／Weightを渡す。AnimatorController／AnimatorControllerPlayableはLegacy Bridge、Editor Preview、比較Probeへ隔離し、削除してもMobPlan、未来評価、切断Predictionの公開契約を変更しない。

現在表示と未来評価は同じ対象Stepへ解決済みの`ExplicitAnimationState`から分岐する。Near Mobの表示Backendも独自にClip遷移やPhase進行を決めず、ゲーム側Stateを消費する。V1予測対象NPCではLook、腕IK、Foot IK等のプロシージャルPose Layerと左右反転を双方で無効化し、現在表示だけに適用しない。後段で再導入する場合は、Layer入力Snapshot、weight／MirrorMode、適用順、Generation、Identityを`ResolvedAnimationPoseInput`へ加え、全Backendで同じ意味を適用する。命中時に実際のBone Poseをスナップショットして最終証拠とする既存規則は維持し、予測State、Root Pose、代表骨Pose、Plan／Asset Identityが許容範囲外なら成果物を破棄して実姿勢から通常の後追い切断へ戻す。

自由飛行のO(1)固定刻み直接予測は外部Probeで技術成立を確認済みとする。T-017では本体のSnapshot、WorldPhysicsProfile、FixedStep、重心／Actor原点および回転処理との統合回帰を確認する。Probeのケース数、誤差値、性能倍率、暫定許容値は本体の製品保証へ転記しない。

### 19.4 局所Prediction Physics

通常世界とは別の`PhysicsScene`に、対象Rigidbody、到達までに接触し得る近傍Rigidbody、周辺静的Collider、必要な外力からなる局所物理島を複製する。対象自身または局所予測に必要な動的近傍に、初期版で未対応のBuildingWorldD6Constraint／ProvisionalSeparationConstraintがある場合は、その候補を既存の後追い処理へ送る。判定は既存候補情報とシステム所有Constraintの識別で行い、一般Joint探索、D6複製、拘束を外した代替予測を追加しない。適用可能な候補だけ固定時間刻みで到達予定時刻まで手動シミュレーションし、その未来姿勢から切断を開始する。

- 静止・解析予測で足りる対象はPhysicsSceneへ入れない。
- 予測シーンはプールし、同じ斬撃波の候補間で共有する。
- 未来ステップは複数フレームへ分散し、スパイクを避ける。
- ユーザー介入、範囲外衝突、スクリプト外力、Animation遷移、別切断を無効化要因として記録する。
- 完全な決定性に依存せず、実接触時に位置差、回転差、対象・Mesh・Physics・Animationの各Generationを照合する。19.5.1の初期リベースは自由飛行の解析予測対象だけとし、局所Physicsの接触・転動対象へ姿勢Gate緩和を広げない。

### 19.5 スケジューリングとCommit

表示出力には4.5.6の直接配置・必要転送と公開条件を適用し、物理適用とは分離する。命中済み表示仕上げはClass 2、投機準備は既存の予測Classで進め、必要なMetadataを物理から隠さない。有効な準備済み最終配置は再転送せず採用し、以下の実命中・面・世代条件を維持する。

初期優先度は4.4の固定`PriorityClass -> Deadline -> EnqueueSequence`だけで決める。未完了依存はDAG CoordinatorがReady投入前に解決し、推定費用はDispatchBudgetの粗い控除、命中確率は受付前Filter、一時描画費用はProfiler Counterとして保持する。これらを単一の動的Scoreへ合成するのは実測後のV2候補とする。遠距離候補はBackground枠の空き時間で処理し、近距離候補はDeadlineを優先する。

投機ジョブは`SlashId`、`SlashGeneration`、命中した`FrontEdgeId`、確定した`SlashFrame`、`ObjectId`、`BaseObjectGeneration`、共用Geometry・物理・Animation・MobPlanの各Generation、予測到達時刻を保持する。Commitには対応するFrontEdge Sweepの`HitConfirmed`を必須とし、識別子、SourceSlashPlane、世代、予測前提のいずれかが一致しない結果は適用せず回収する。19.5.1の対象剛体だけはWorld姿勢一致をLocal Plane採用Gateへ置き換え、その操作に確定した面・基底frameと一致するGeometryだけを使用する。これにより、Candidate Flight Boundsへ入っただけの空振り候補や、古い非同期結果が新しい切断状態を上書きすることを防ぐ。


上記は通常の命中切断成果物のCommit条件である。7.9の確定後の任意分割・退役は非命中公開条件に従い、公開後は旧所有構成向けの予測成果物を拒否する。任意候補の準備・見送りだけでは通常予測を失効させない。

#### 19.5.1 剛体切断成果物の実姿勢リベース

**目的と初期Scope。** 物体を予測World姿勢へ移動せず、予測時に仮決定した切断前物体ローカルの面と、その面から先行生成したGeometryを実姿勢に取り付ける。姿勢誤差を消す方式ではない。面接線方向の並進差は吸収しやすいが、法線方向の差と回転差は切断位置／向きへ残る。同一Slash内の対象ごとの面差と、近似検査で検出しきれない局所的な切断縁／VFX差を許容する。

初期対象は、単一RigidBody frameで全対象Geometryを表せる、点Anchorなし・接触なし・システム所有Constraintなしの自由飛行剛体だけとする。Geometry／local shape pose／scale／Topologyが予測開始から命中まで不変であり、一定重力、linear／angular dampingなし、外力／外部Torque介入なし、FixedStep境界、予測Horizon 0.5秒以下を要求する。点Anchor付き建物、BuildingWorldD6Constraint／ProvisionalSeparationConstraint付き対象、Skinned Mesh、骨相対Pose変化、再切断／Mesh世代変更、可変scale、shear、接触／転動は対象外とする。scaleは事前にGeometryへ固定し、予測・実姿勢の写像は正規直交回転＋並進だけとする。将来の局所Physics／Skinned Root拡張は別判断とし、D-046のMobPlan／骨Pose検証を緩和しない。既存の接触／介入履歴と予測前提Snapshotで条件を確認できなければ対象外とし、初期版で完全な無衝突証明器を新設しない。

**面とframeの正本。** 面は正規化法線nと距離dの4係数で表し、符号は `dot(n,x)+d=0`、Positive／NegativeはSource面からの向きを保つ。符号を任意反転して子IDを交換しない。切断前のParentLogicalFragmentLocalId、BaseObjectGenerationと固定Geometry-to-Physics-frame写像をLocal Planeのframe identityとする。PredictedObjectPose／ActualObjectPoseはこの同じframeからWorldへの変換であり、重心位置をMesh原点として代用しない。

| データ | 正本・寿命 |
| --- | --- |
| SourceSlashPlane | Latch済みSlashFrameの不変World面。有限FrontEdge Sweep、HitConfirmed、Slash飛翔VFX、攻撃方向、因果Traceに使用 |
| PredictedObjectLocalCutPlane | 命中前の予測Poseから求める仮Local Plane。Geometry Jobの完成とは独立した小さいimmutable Descriptorとして先に公開する |
| SelectedObjectLocalCutPlane | 命中時に操作単位で一度だけ確定する面。予測面採用または実命中面Fallbackのどちらかであり、全子と後着成果物の共通入力 |
| CommittedCutPlane | 採用時はActualObjectPoseから再構成するWorld面。以後は親から子へ引き継いだLocal Planeを各Actorの現行frameへ写したもの。命中時World位置へ固定しない |

列ベクトルの同次変換をT、planeの4係数をπとすると、`πlocal = transpose(Tpredicted) * πsource`、`πworld = inverseTranspose(Tactual) * πlocal`とする。点の変換でplane normalを処理せず、nとdを同じ正の長さで正規化する。実命中Fallbackは同じ規則でTactualからπsourceをlocalへ変換する。有限性、法線非zero、既存Geometry Kernelの数値／frame前提を検証する。選択後に係数を再推定・再量子化せず、子frameへの必要な座標変換だけを系譜から行う。World座標で生成した予測成果物は入力frameへ安全に還元できる場合だけ利用し、単にActor Transformへ予測位置を代入しない。

**命中時の一回選択。** 予測面DescriptorはSlashId／SlashGeneration／対象FrontEdgeId／SourceSlashFrame、ObjectId／BaseObjectGeneration、ParentLogicalFragmentLocalId、Mesh／Physics／Topology世代、予測基底・対象FixedStepId、WorldPhysicsProfileとRebase Profileのidentity、固定local frame写像を保持する。実命中時の検証順は対象Scope、Descriptor有無、identity／世代／Step／前提、数値／幾何条件、視覚Gateとし、不一致を姿勢リベースで隠さない。初期版では予測対象FixedStepIdと実命中SnapshotのStepを一致させ、連続時刻の外挿で代用しない。Mesh／Collider JobのReady、成功／失敗、残り時間は面の採否入力へ含めない。

1. SourceSlashPlaneに属する実FrontEdge SweepのHitConfirmedを必須とし、候補列挙だけでは面や切断を公開しない。
2. ObjectGeneration更新前の実Physics poseと世代を同じStepのSnapshotとして取得する。Mesh表示の補間Transformを物理Snapshotの代わりにしない。
3. Gate合格ならPredictedObjectLocalCutPlane、不在／不合格なら実命中から求めたLocal PlaneをSelectedObjectLocalCutPlaneとする。実姿勢からも有効面を構築不能な場合は通常の切断失敗・回収経路を使い、不正Planeを公開しない。
4. Pending Cut登録・世代更新・表示変更より前に、同じ実SnapshotとSelectedObjectLocalCutPlaneで7.6の現在採用Convex集合を分類する。片側空No-opまたは`MaxIncompleteCutOperationCount`到達時は面Descriptorをゲーム状態へ公開せず、CutOperationId、LogicalCutOperation、子、境界、Pending仕事を作らない。この判定にもMesh／Collider JobのReadyを使用しない。
5. 受付を通過した場合だけ、正のCutOperationIdを持つ同じ切断のPending Cut登録・基底から次世代への遷移・Local Plane確定を原子的に行う。この時点ではLogicalCutOperation、論理子、CutBoundaryRecordを公開しない。Operation公開前のTemporary clip／Capと局所VFX、点Anchor配分と所有者単位の固定／動的の導出、Provisional配分・OBB質量近似・分離Constraintは同じ面を読む。最初にSource面で表示して後から予測面へ切り替える二段階公開を禁止する。
6. 選択Descriptorを根拠に、既存の対応Jobを継続／優先化する。未発行なら同じ基底Geometryと選択面で後追いJobを投入し、面採用のためにJob.Complete、同期切断、同期cookを行わない。不採用面のSchedule済みJobは完了後に回収する。

基底世代は命中したPending Cut自身による既知の次世代への更新と結び付けて保持し、単に現在世代と旧BaseObjectGenerationが異なることだけで対応成果物を破棄しない。一方、別切断や外部変更で系譜が進んだ成果物は既存Generation GateでRejectする。Snapshot取得からPending Cut公開までに前提が変化した場合は古い選択を公開せず通常の再評価へ送る。Pending Cut公開後はPlane採否を再実行せず、後にLogicalCutOperationを公開する場合も同じ面を継承する。後着Job失敗、予算超過、Collider包含不合格、View変化でも元Source面へ切り替えず、確定面での再計算または利用可能な既存物理表現の正式採用を使う。

**boundedな初期視覚Gate。** `RigidCutRebaseProfileV1`はversion／content identity、有限・非負の`MaxPlaneNormalAngleRadians`（π未満）、`MaxPlaneFieldErrorMeters`、`MaxProxyPointPixelError`を持つ。測定条件は判定時の左右眼View Projectionと各viewport pixel寸法へ固定する。未設定／不正Profile、片眼情報欠落ではリベースを採用しない。値の校正はO-047で行い、Probeの1 mm／0.5度／5 mmや画面幅1%を自動採用しない。

Mesh Jobとは独立した基底Geometryの保守的local Boundsの8 cornerを実poseへ写した固定8点だけを使用する。正規化したSource面(nS,dS)と候補World面(nC,dC)について、法線角度と`maxCorner(abs(dot(nC-nS,p)+dC-dS))`を検査する。後者はBounds内のsigned plane field差の上限であり、全切断線の距離上限ではない。同じ各cornerから両面への直交投影点を作り、左右眼それぞれで対応2点のpixel距離を比較し、その最大をProxyPointPixelErrorとする。生成済みCap／Mesh頂点をsample選択へ使用せず、Job Readyによって点集合を変更しない。非finite、Near Plane上／背面を含む投影不能、無効Boundsは不採用とし、画面外の点をclampして誤差を小さくしない。全条件が閾値以下のときだけ採用する。

これは8点の近似であり、真の切断線／シルエット最大pixel誤差、注視追跡、VFX全頂点比較、接線付近のTopology一致を保証しない。多少の未検出差を許容し、全Contour生成や全Triangle走査を採用Gateへ追加しない。World上限も併用し、遠距離で見えにくいことだけを理由に無制限の面差を許さない。有限Sweepの命中対象を増やさず、HitConfirmedを取消・偽装せず、同一Slashの別対象にもリベース面を伝播しない。

**後続物理と表示。** 採用するのはGeometryと操作Local Planeであり、予測速度、予測World COM、支持・外界接触・安全判定の結果ではない。実Actorの最新pose／速度と既存の質量Budget・分離Impulse一回規則を使う。選択時のActualObjectPoseへ後日Actorを巻き戻さず、各子のPhysics Frameに保持したGeometryをその時点のActorへ取り付ける。Finalは由来Provisional Convex内の包含検証を引き続き必須とする。攻撃Impulseの意味方向はSource Slash、分離Constraint／幾何Offsetの法線は採用面として区別し、両者が同じ法線であると仮定しない。必要な点配分や物理公開条件が未確定の間は既存Work依存で物理適用を待ち、成立不能と確定した場合は利用可能な既存物理表現を正式採用した`Stable Unsplit`へ終端する。リベースを理由に新しい外界penetrationや固定側Impulseを許可しない。Render補間は既存Actor従属の範囲だけで行い、表示―物理誤差蓄積／すり合わせ状態を復活させない。

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

Jobベイクの導入採用時は、斬撃波候補がモブへ到達する時刻を`MobPlan`上でサンプルし、未来Rig Poseから4.5.2のJobベイクによるVP入力準備と骨Physics Proxyの姿勢化へ分岐する。Phase 4.7は候補の準備要求・結果取得・失効まで、Phase 5.1は共用Geometryへの切断面適用、骨Physics Proxy分類、4.5.6の正負Index直接出力・転送および実命中での再利用までを接続する。成果物は既存Work Token／入力Snapshot／世代・Identity契約に相関し、実命中時に既存の採用条件を再確認する。保持形状は固定せず、既存入力Snapshotを共有参照でき、各成果物への相関情報の重複保持を要求しない。計画が維持されていれば遠距離ほど完成済み成果物を再利用でき、未完成・不採用時は4.5.2の現在Pose同期経路と既存の後追い・回収へ接続する。

計画生成自体がフレーム予算を圧迫しないよう、Mob Future PlannerもFuture Evaluation SchedulerのWork Itemとして実行する。近距離で命中Deadlineを持つ姿勢生成は`NearDeadlinePrediction`、遠距離MobPlanの延長は`BackgroundMaintenance`へ固定し、`CriticalPhysicsSafety`／`ConfirmedPhysics`／`ConfirmedGeometry`より先にScheduleしない。

### 20.6 無効化と観測

主な無効化要因は、プレイヤーの介入可能領域への侵入、NavMesh変更、経路上の新障害、Behaviorの高優先Intent、Animation遷移、外力、対象の切断である。予約機能を導入した後は別モブとの予約競合も無効化要因へ加える。`MobPlanCreated`、`MobPlanExtended`、`MobTierChanged`、`MobPlanInvalidated`、`MobReplanned`、`MobPredictionUsed`、`MobPredictionRejected`をTraceへ記録し、`ReservationCreated`は予約機能導入後にだけ記録する。`MobId`と`PlanGeneration`から依存Taskを辿れるようにする。

V1の無効化粒度は単一Mob Planまたは固定Mob Group全体だけとし、影響依存を厳密に解析しない。無効化は`PlanGeneration`を進め、未再生Sample、未来Skeleton Pose、依存する投機的切断成果物を同じ世代検証でStaleにする。Player Bodyはプロップ等と非接触であるため、単なるPlayer Physics Contactを無効化要因に要求せず、攻撃、介入領域、Script Intent、Path変更、対象切断などゲーム側で観測可能なEventを正本とする。

### 20.7 段階導入とFuture Works

Phase 4.7の最初のPlayable実装は、固定ステップ二相更新、Waypoint／Lane Desired Motion、固定長未来Queue、Rootと`ExplicitAnimationStateV1`全体の再生補間、Loop／Clamp Clip Catalog、移動距離由来Phase、粗い世代無効化、既存Dispatcherへの補充投入までとする。4.65でJobベイクを採用した場合だけ、候補の未来Pose／VP準備要求・結果取得・失効を追加し、人形の実命中・先行切断完成・Commitの統合確認を5.1へ分ける。不採用時は計画単体で完了し、先行切断の統合を要求しない。V1予測対象NPCではプロシージャルLook／腕／Foot IKと左右反転を現在／未来の双方で無効化する。この段階ではモブ同士の多少の重なり、遠方Mobの短時間停止、全Plan／Group再計算、Clip hard switchのPose popを許容し、ORCA、細粒度依存解析、Pose Layer／Mirror再導入を正しさの条件にしない。

重なりがプレイ上またはT-092の実測で問題になった場合だけ、次段として固定容量Uniform Grid、固定Cell走査順、`MaxNeighbors`、固定順Constraintを持つbounded ORCAを同じ`MobTrajectoryKernel`のDesired Motion後段へ追加する。ORCA追加後もFar／Dormantへ完全な群衆衝突を必須にせず、Tierごとに無効化できる。Grid／Neighbor／作業領域の容量超過ではMobを黙って省略せず、その計画GroupをORCAなしのLane追従またはHoldへ固定的に低下させる。

空間／Mob Group Chunk、Active／Candidate Interaction記録、Reverse Dependency DAG、Tick単位の部分再計算、新規Interaction用Guard Band、Flow Field、軌道圧縮はFuture Worksとする。これらは再計算量を減らす最適化であり、欠落しても全Plan／Group単位の再生成で正しく動作する。Flow Fieldを追加する場合も`DesiredMotionProvider -> MobTrajectoryKernel`境界だけへ接続し、ORCAや未来QueueがPath実装を直接参照しない。

## 21. 観測・トレース設計

Phase 0～0.11は21.1～21.15の既存Trace仕様と受入条件だけで完了判定する。Phase 0.12～0.14は完了済みのTrace runtimeとWriter APIを破壊的に置換できる独立した後続移行であり、旧Phaseの成果物、受入条件および既存Artifactの読込みを遡及変更しない。後続移行の差分は21.16だけを正本とし、旧節へ新旧規則を混在させない。

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

7.9の任意処理は既存Task lifecycleと少数Counterで通常切断と区別する。所有者再編成全体を復元する専用Trace束や新しい永続形式を要求せず、既存のOperation作成束は実際の切断履歴だけを表す。Capture／Traceの形式・完全性・非待機契約を変更しない。

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
Zantetsu.Support.CommitValidate
Zantetsu.Commit.Validate
Zantetsu.Commit.Apply
Zantetsu.Trace.Drain
Zantetsu.Capture.Copy
Zantetsu.Capture.Encode
```

論理Work ItemのSchedule、Job開始、完了、CommitをProfiler Flow IDで結び、CPU Profiler Timeline上でスレッドをまたぐ依存関係を確認できるようにする。集計値にはProfilerRecorderまたはカスタムCounterを使用する。

未来用JobベイクはPhase 4.65の限定実装で代表的な複数候補の同時要求を同期経路と比較し、Pose準備、Schedule／完了回収、AoS処理を含むMain Thread負荷と、Worker費用・結果到着時間を分けて確認する。既存Profilerと小さい比較記録を使い、4.5.2の品質とMain Thread負荷削減の結果から人間が導入の採否を決める。効果がなければ同期BakeMesh維持を正常な完了結果とし、本体接続を要求しない。採用時は対応範囲・実装構成を決めて本体へ接続し、5.1で転送等の残る費用と先行切断完了率・採用状況を確認する。比較と採否決定を省いて4.65完了とせず、Phase 5の基本動作はその結果待ちにしない。固定高速化率、全候補の翌フレーム完成、大規模試験matrix、新しい計測schemaは要求しない。7.5／T-076の`UnityBakeMesh`はCollider用Physics.BakeMeshであり、SkinnedMeshベイクを混在させない。

提案書が引用する外部の非同期スキニング参考測定は、原資料を本リポジトリでは未検証の参考情報として扱う。本体への導入判断はPhase 4.65の比較結果に基づき、外部測定のCPU・構成・数値・実装例を成立条件や必須Fixtureにしない。

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

最低限記録するイベントは、`BladeTrackingLost`、`BladeTrackingRestored`、`BladeSamplesReset`、`EdgeGateEntered`、`EdgeGateRejected`、`SlashPrimed`、`SlashLatched`、`SlashFrontCreated`、`FrontVertexAdded`、`FrontEdgeActivated`、`FrontSampleIgnored`、`FrontTopologyRejected`、`SlashFinalizedByReversal`、`SlashFinalized`、`SlashFrontExpired`、`SlashRecoveryStarted`、`SlashRearmed`、`FrontHitConfirmed`、`CandidateDetected`、`TaskScheduled`、`TaskStarted`、`TaskCompleted`、`PredictionValidated`、`PredictionRejected`、`GenerationChanged`、`MobPlanCreated`、`MobPlanExtended`、`MobTierChanged`、`MobPlanInvalidated`、`MobReplanned`、`MobPredictionUsed`、`MobPredictionRejected`、`CaptureFrameQueued`、`CaptureFrameEncoded`、`CaptureFrameDropped`、`CaptureRingFrozen`、`ProjectionCaptureCopied`、`CommitStarted`、`CommitSucceeded`、`CommitRejected`、`FallbackActivated`、`TaskCancelled`、`ResultDisposed`とする。予約機能実装時には`ReservationCreated`をappend-onlyで追加する。切断Operation公開の実装時には`LogicalCutOperationCreated`、`LogicalCutOperationChildLinked`、`LogicalCutOperationBoundaryLinked`、`LogicalCutOperationBoundaryEndpointLinked`、`LogicalCutOperationTraceCompleted`、`LogicalCutOperationRejected`を追加する。点Anchorによる固定と物理分裂は既存物理状態・Commitイベントで記録する。Trace完全性実装時には`TraceIntegritySummary`をappend-onlyで追加する。Provisional物理実装時には`ProvisionalPhysicsCommitted`、`ProvisionalPhysicsFallbackActivated`、`ProvisionalPhysicsFinalized`、`ProvisionalPhysicsSafetyFrozen`をappend-onlyで追加する。Player非接触移動実装時には`PlayerLocomotionRejected`をappend-onlyで追加する。剛体リベース実装時には`RigidCutPlaneNormalXY`、`RigidCutPlaneNormalZDistance`、`RigidCutPlaneSelected`をappend-onlyで追加する。片側空No-opと受付上限見送りには専用Trace Eventを追加しない。既存Event名の`Task`は論理Work Itemを指し、`TaskId`をFragment識別子へ流用しない。`TaskCancelled`は原則としてSchedule前の取消または取消可能なI/O処理にだけ使用し、Schedule済みJobの不採用は`PredictionRejected`／`CommitRejected`と`ResultDisposed`で表す。

`LogicalConvexFragmentLocalId`は0を未設定用に予約した正の32bit `int`とし、`ObjectId + ObjectGeneration`をスコープとして一意かつ同一世代内で再利用しない。`CutOperationId`、`LogicalFragmentLocalId`、`CutBoundaryLocalId`は0を未設定用に予約した正の32bit `int`とし、`ObjectId`の生存期間全体で種別ごとに一意かつ非再利用とする。`LogicalCutOperationCreated`の共通`ObjectGeneration`は`ParentObjectGeneration`を格納し、宣言型`uint`の全域を許可する。その他のOperation系Eventの共通`ObjectGeneration`はEvent発生時の現世代を記録する。Traceの`ObjectId`と各Cut系LocalIdを組み合わせて対象を復元する。doubleへ格納するID、序数、件数は非負int範囲、Generationは`uint`全域とし、いずれもIEEE 754 binary64で整数精度を失わない。イベント別の固定フィールド割当は次を正本とし、汎用的なFrom／To State遷移と混同しない。

| EventType | FromState | ToState | Reason | Value0 | Value1 |
| --- | --- | --- | --- | --- | --- |
| `RigidCutPlaneNormalXY` | CutOperationId | ParentLogicalFragmentLocalId | `None` | Selected local normal.x | Selected local normal.y |
| `RigidCutPlaneNormalZDistance` | CutOperationId | ParentLogicalFragmentLocalId | `None` | Selected local normal.z | Selected local plane distance d |
| `RigidCutPlaneSelected` | CutOperationId | `RigidCutPlaneChoice` 1または2 | 採用は`None`、Fallbackは専用Reason | ParentLogicalFragmentLocalId | BaseObjectGeneration |
| `CaptureFrameAdmissionRejected` | 0 | 0 | `None` | `CaptureFrameAdmissionRejectKind` | `FrameDraftRegistryFull(5)`。共通CaptureFrameIdは0 |
| `CaptureFrameDropped`（通常Draft理由6～8） | `Pending(0)` | `Dropped(2)` | `None` | 0 | `PngEncodeFailed(6)`／`PngStagingStoreFull(7)`／`CaptureCancelled(8)`。共通fieldは対応Draft Trace Contextから転記 |
| `CaptureFrameDropped`（freeze terminal理由9） | `Pending(0)` | `Dropped(2)` | `None` | 0 | `FreezeDrainTimeout(9)`。通常Queueを通さずterminal Bufferへだけ構築 |
| `LogicalCutOperationCreated` | ParentLogicalFragmentLocalId | 0（未使用） | `None` | CutOperationId | 0（未使用）。ParentObjectGenerationは共通ObjectGeneration |
| `LogicalCutOperationChildLinked` | DirectChildLogicalFragmentLocalId | 0（未使用） | `None` | CutOperationId | 0始まりChild序数 |
| `LogicalCutOperationBoundaryLinked` | CutBoundaryLocalId | 0（未使用） | `None` | CutOperationId | 0始まりBoundary序数 |
| `LogicalCutOperationBoundaryEndpointLinked` | CutBoundaryLocalId | DirectChildLogicalFragmentLocalId | `None` | CutOperationId | EndpointSlot。正側=0、負側=1 |
| `LogicalCutOperationTraceCompleted` | DirectChildCount | CutBoundaryCount | `None` | CutOperationId | 当該Operation作成Trace束の期待Event数 |
| `LogicalCutOperationRejected` | 0 | 0 | `InvalidLogicalCutOperation` | 割当済みなら候補CutOperationId、割当前なら0 | `LogicalCutOperationValidationError` bit mask |
| `TraceIntegritySummary` | 現Runの`TraceIntegrityState` | TraceCaptureOverflowCount（0～`int.MaxValue`） | 現Run完全なら`None`、enqueue失敗なら`TraceWriteFailureObserved`、それがなくcapture容量超過なら`TraceCaptureOverflowObserved` | SealedTraceEnqueueFailureCount | PriorBundlePublishFailureCount。監査専用で現Run完全性には不使用 |
| `ProvisionalPhysicsCommitted` | Commit前`FragmentGroupPhysicsState` | `ProvisionalPhysicsSplit`または`ProvisionalAnchoredSplit` | `None` | CutOperationId | 公開Provisional Actor数 |
| `ProvisionalPhysicsFallbackActivated` | 構築試行時の実`FragmentGroupPhysicsState` | FromStateと同じ実状態。Provisional未公開なので状態遷移を表さない | 専用Fallback Reason。`None`禁止 | CutOperationId | 要求した`ProvisionalPhysicsSplit(5)`または`ProvisionalAnchoredSplit(6)`。その他は禁止 |
| `ProvisionalPhysicsFinalized` | `ProvisionalPhysicsSplit`または`ProvisionalAnchoredSplit` | `StableFastCook` | `None` | CutOperationId | Final Actor数 |
| `ProvisionalPhysicsSafetyFrozen` | `ProvisionalPhysicsSplit`または`ProvisionalAnchoredSplit` | `ProvisionalFaultFrozen` | Primary `ProvisionalRuntimeFaultReason`。`None`禁止 | CutOperationId | `ProvisionalFaultContainmentDisposition`。`Invalid`禁止 |
| `PlayerLocomotionRejected` | `PlayerLocomotionPolicy` | 0 | `None` | 次姿勢の侵入深度 | 現姿勢の侵入深度 |

`PlayerLocomotionRejected`の侵入深度は同一の`PlayerLocomotionOccupancy`と距離単位で評価したfiniteかつ0以上のbinary64とする。拒否判定に用いた現姿勢と候補次姿勢の値をそのまま格納し、表示Mesh Triangleや旧Colliderから事後再計算した値へ置き換えない。

Capture Draft Registry実装時には`CaptureFrameAdmissionRejected`を`TraceEventType`へappend-onlyで追加する。これはID発行前の受付拒否専用であり、正のIDを発行済みの処理だけを表す`CaptureFrameDropped`と同じID相関として解釈しない。共通`CaptureFrameId=0`、`Value0=CaptureFrameAdmissionRejectKind`、`Value1=FrameDraftRegistryFull(5)`へ固定する。`CaptureFrameAdmissionRejectKind`は固定値`None=0`、`PendingLimit=1`、`RunEntryLimit=2`とし、0および未知値をEvent生成時にRejectする。

剛体リベースのReasonには`RigidRebaseOutOfScope`、`RigidRebaseCandidateUnavailable`、`RigidRebaseIdentityMismatch`、`RigidRebaseInputInvalid`、`RigidRebaseWorldErrorExceeded`、`RigidRebaseProjectionUnavailable`、`RigidRebasePixelErrorExceeded`を追加し、19.5.1と本節の固定順・Trace束へ対応させる。

点AnchorとOperationのReasonには`AnchorGenerationMismatch`、`InvalidLogicalCutOperation`を追加し、Trace完全性には`TraceWriteFailureObserved`と`TraceCaptureOverflowObserved`を追加する。Provisional公開前Fallbackには`ProvisionalActorCapacityExceeded`、`ProvisionalShapeCapacityExceeded`、`ProvisionalConstraintCapacityExceeded`、`ProvisionalGeometryShareUnsupported`、`ProvisionalShapeClassificationInvalid`、`ProvisionalMassApproximationInvalid`、`ProvisionalActorCreationFailed`、`ProvisionalConstraintCreationFailed`、`ProvisionalGenerationMismatch`、`ProvisionalAtomicCommitFailed`を追加し、`ProvisionalPhysicsFallbackReason` 1～10へ同順で一対一対応させる。Provisional公開後Faultには`ProvisionalNonFiniteActorState`、`ProvisionalConstraintRuntimeFailed`、`ProvisionalLinearVelocityLimitExceeded`、`ProvisionalAngularVelocityLimitExceeded`を追加し、`ProvisionalRuntimeFaultReason` 1～4へ同順で一対一対応させる。Snapshot不在と封じ込め検証失敗はTraceReasonにせず、`ProvisionalPhysicsSafetyFrozen.Value1`の`ProvisionalFaultContainmentDisposition` 2／3へ格納する。いずれも既存`TraceReason`の次の未使用値へappend-onlyで明示値を割り当て、既存値を変更・再利用しない。Reject／Fallbackイベントは専用Reasonを必須とし、Reason enumを`Value0`／`Value1`へ重複保存しない。

Provisional物理のCommitted／Finalized／ProvisionalFaultFrozen状態遷移と、状態を変えない公開前Fallback Outcomeは、それぞれゲーム側でexactly onceに確定する。対応する4種のTrace Eventの構築／enqueueは結果を消費する単一Coordinatorが最大1回だけ試行し、enqueue成功時は当該結果につき厳密に1件、失敗時は0件としてゲーム状態をrollback／再TraceせずRunをIncompleteにする。同じ結果から2件以上の同種Eventを生成しない。4 Eventの共通`ObjectGeneration`は対象結果のGeneration、`Value0`のCutOperationIdはObject生存期間中の正の非再利用IDとする。公開前FallbackはFromStateとToStateを同じ実状態に固定し、要求Provisional種別を`Value1`から復元する。ProvisionalFaultFrozenは状態公開をTrace成功へ依存させない。Fallback／Faultでは内部ReasonとTraceReasonの固定対応を検証し、不正値なら部分物理を公開せず、公開前は既存Group維持、公開後はGroup全体のScene除外へfail closedする。

`LogicalCutOperationValidationError`は固定bit `InvalidId=1<<0`、`InvalidGeneration=1<<1`、`ChildCountOutOfRange=1<<2`、`BoundaryCountOutOfRange=1<<3`、`DuplicateChildId=1<<4`、`DuplicateBoundaryId=1<<5`、`ParentChildAlias=1<<6`、`UnknownReference=1<<7`、`BoundaryOutsideDirectChildren=1<<9`とし、複数違反をORして記録する。未知bitはRejectし、同じ入力から同じmaskを得る。Operation作成成功時のTrace束は`Created`、全Child Linkを序数順、各Boundary Linkと実在するSideの子参照をBoundary序数順、最後に`TraceCompleted`の順とする。期待Event数は`2 + DirectChildCount + CutBoundaryCount + 実在するSide子参照の総数`であり、EndpointSlotは正側0・負側1とする。存在しないSide子参照や同じSlotの重複を発行しない。境界0件ではBoundary系Eventを生成しない。`TraceCompleted`があり、期待Event数、ID、序数、境界のSide・子参照、Generationが一致する束だけを完全Operation Traceとして扱う。異なる二物理所有者を結ぶEdgeの証明には使わない。不正構築時は作成Trace束を発行せず`LogicalCutOperationRejected`だけを記録する。

現行の固定サイズTrace Eventと`Value0`／`Value1`を維持し、Operationイベント追加だけを理由にバイナリレコード構造を変更しない。Operation系6イベントでは`Value0`をCutOperationId専用としてTimelineが整数表示・検索する。`FromState`／`ToState`は通常の汎用状態遷移ではなく上表のイベント固有割当として解釈する。`TraceEventType`と`TraceReason`の数値はappend-onlyとし、既存値の変更や再利用を禁止する。

Operation状態の公開とTrace enqueueは意図的に非トランザクションとする。受付時にはOperation作成Traceを発行せず、通常処理で確定した子・境界が構築Validatorへ合格した後、Fragment／Boundary／Operationをゲーム状態へ原子的に公開してから、メインスレッドで固定長の作成Trace束をbest effort enqueueする。TraceLoggerの破棄、容量／Nativeエラー等で途中失敗しても公開済みOperationを巻き戻さず、Trace書込経路とは独立したsaturating `uint`の`TraceEnqueueFailureCount`を増やす。末尾`LogicalCutOperationTraceCompleted`がない束、件数やTopologyが一致しない束はTimelineで`IncompleteOperationTrace`として表示し、状態再現・Golden比較・T-074合格根拠には使用しない。Trace失敗をゲーム状態のCommit失敗や`LogicalCutOperationRejected`として偽装しない。

Jobからは`NativeQueue<TraceEvent>.ParallelWriter`等のBurst互換経路へ書き込み、メインスレッドがフレーム末尾に回収する。毎フレーム全状態をスナップショットせず、状態遷移と重要な判断だけを記録する。


剛体切断の`RigidCutPlaneChoice`は`None=0／PredictedLocal=1／ActualHitLocal=2`とし、0は未選択で公開禁止とする。19.5.1の判定順に最初の失敗を`RigidRebaseOutOfScope／RigidRebaseCandidateUnavailable／RigidRebaseIdentityMismatch／RigidRebaseInputInvalid／RigidRebaseWorldErrorExceeded／RigidRebaseProjectionUnavailable／RigidRebasePixelErrorExceeded`のappend-only TraceReasonで記録する。採用理由へMesh未完成／Bake遅延Reasonを使用しない。PhysicsやGeometry Jobの失敗は既存の各Eventで別に記録する。

各有効な剛体Pending Cutのゲーム側面確定後、`RigidCutPlaneNormalXY -> RigidCutPlaneNormalZDistance -> RigidCutPlaneSelected`の固定3 Eventを各1回だけ構築／enqueue試行する。共通ObjectGenerationはPending Cut受付で更新した世代、FixedStepは選択SnapshotのStep、Slash／FrontEdge／Object識別子は全3件で同じとし、FromStateのCutOperationIdは受付時に発行した正・Object生存期間内非再利用IDを使用する。TaskIdは0とし、特定の完成JobまたはLogicalCutOperation公開に面選択を結合しない。ParentLogicalFragmentLocalIdがLocal Planeの基底frameを指し、SelectedのValue1で更新前世代を復元する。4係数は実際に選択した値をそのままbinary64 Valueへ記録し、表示用丸めを行わない。選択結果はTrace成功へ依存させず、enqueue失敗時にゲーム状態rollback／再試行をせず既存Run Incomplete規則へ送る。

Timeline／検証器はObjectId＋CutOperationIdで束を結合し、型順、3件の件数、全共通相関、Parent ID、Base世代、有限かつ正規化されたPlane、Choice／Reasonの許可組合せを検証した場合だけ採用面を復元する。LogicalCutOperation未公開中は完全な3 Event束を受付済みPending Cutの面として扱い、後にOperation作成Traceが存在する場合は親、世代、CutOperationId、面との相関一致を追加検証する。末尾Selectedだけ、欠落、重複、別操作／世代混在を完全な選択とみなさず、履歴上書きや旧Traceの記録なしは復元不能として扱う。動く子frameからのWorld面再構成には既存の姿勢／系譜情報を必要とし、この3件だけで全物理軌道や採用Gateの再実行を証明しない。新しい可変長Trace payloadや物理すり合わせ状態を導入しない。

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
- `SlashId`、`ObjectId`、`ObjectGeneration`、`MobId`、`PlanGeneration`、`TaskId`、`CutOperationId`、失敗理由で絞り込む。Operation系Eventでは作成Trace束の完了性と境界のSide・子参照を復元し、不完全束を明示する。
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

Phase 0.11の責務は次の4系統へ固定する。ここで「単一論理Consumer」は固定2本のOrdered Submit WorkerとOrdered Output Workerから成り、Plan I/Oを行う非所有Publication Service Workerとは別である。Main／Render Thread Producerは固定容量資源をAccepted前に予約し、Accepted線形化点でWork Tokenを固定Submission Queue末尾へ厳密に1回入れて待たずに戻る。このFIFO挿入順がAccepted順の正本であり、Work TokenのSlotIndex、Generation、CaptureFrameIdまたは受付時刻をsort keyとして再構築しない。Submit WorkerはSubmission Queueを単独消費し、先頭Workについて`NvEncEncodePicture`成功なら`Submitted`、submit前の制御可能な失敗／取消なら`FailedBeforeSubmit`を固定Submit-to-Output Queueへ同順で厳密に1件渡す。Output Workerは同Queueを単独消費し、`Submitted`では先頭Eventだけを待ってBitstream ownershipを取得してInput Surface返却、chunk append、streaming hash／ByteLength更新、Frame Relation追加を行い、`FailedBeforeSubmit`ではappendせず所有資源を安全に解放する。安全な結果または所有権を確認できなければrecord／Completionを推測せずprocess-wide Poisonへ進む。両variantのFrame CompletionはOutput Workerだけが生成する。Main Threadは固定容量Completion Queueをbounded pollしてDraft／Traceの正式状態だけを反映する。単一`NvencCaptureRunCoordinator`は全Queue、Backend／両Worker、Context、Session Ownership Lease、Trace Freeze状態、局所Registry slotおよびDispositionをRun開始からPlan commitまたはIncompleteまで所有し、両Worker静止後のPlan I/Oは所有権を渡さずPublication Serviceへ非同期要求する。

Production Composition Rootは非staticな`NvencCaptureProcessState`をprocessにつき1個だけ生成し、Backend、`NvencCaptureRunCoordinator`、Publication Serviceへ同じ参照をconstructor injectionする。正常または制御可能な失敗で両Worker／Serviceの静止、全Completion、全所有資源解放およびOS lock解放まで確認できた場合だけ`Running -> Draining -> Running`を許可し、制御喪失では`Running`または`Draining`から`PoisonedUntilProcessRestart`へ一方向に進む。ProductionにはReset／Unpoison APIを設けず、Domain Reload、Play Mode再開、Backend再生成またはnative処理の遅延帰還でPoisonを解除しない。Tier Aのfake構成だけはFixtureごとに独立したtest stateを生成し、Production Composition Rootまたは別Fixtureと共有しない。実NVENC BackendはUnity Editor processでは開始前にUnsupportedとし、Tier B／Cおよび実Captureはstandalone Player／専用processで実行する。

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

Phase 0.11の停止順は`StopAccepting -> BeginDrain -> Submit Workerが全Accepted WorkのSubmitted／FailedBeforeSubmit recordを生成 -> Submit Worker Join -> Output Workerが全record、Frame Completionおよび所有資源を回収 -> Output Worker上でchunk FinalizeまたはAbandon -> Context terminal result回収 -> Output Worker Join -> 全資源／予約ゼロ確認 -> Trace Freeze -> Plan commitまたはIncomplete`とし、全体を`NvencCaptureRunCoordinator`が進行する。Submit Worker Join前にSubmit-to-Output Queueを破棄せず、Output WorkerのFinalize／Abandonまたはterminal result回収をOutput Worker Join後へ送らず、各Join成功後に同Workerへ新しい処理を要求しない。Backendの`TryJoin`はterminal処理後のSubmit／Output Worker停止、native teardown、Main Thread側Texture破棄およびBackend資源ゼロを確認した場合だけ成功し、Context terminal要求、Registry、Trace Freeze、DispositionまたはPlan I/Oを実行しない。

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

Draft、Registry、Trace の状態遷移は main thread の共通 Coordinator だけが反映する。Backend はこれらを変更しない。Draft の外部状態は `Pending / Staged / Dropped` を維持し、backend 内部状態を追加しない。Phase 0.11の`Staged`は当該Frameのencode／chunk append成功だけを表し、Run Artifactの公開可能性またはCaptureCompleteを単独では証明しない。形式非依存 Drop Reason は既存 enum へ append-only で追加し、既存 0～9 を再番号しない。Phase 0／0.1のFreeze順と意味は維持するが、Sessionの生成・解放はOwnership Lease APIへ移行する。Phase 0.11ではRun開始時からSubmit WorkerとOutput Workerを同時に稼働させ、Main Thread Coordinatorも完成済みFrame Completionを更新ごとの固定budgetで継続的にbounded pollする。正常／制御可能な失敗の停止順は、StopAccepting、Backend `BeginDrain`とAccepted件数Snapshot固定、両Workerの並行drain、Submit Workerによる全Accepted Workの`Submitted`／`FailedBeforeSubmit` record各1件生成、Submit Worker停止確認、Output Workerによる残存record、`NvencOwnedEncodedAccessUnit`、同期`NvencRunChunkSink`処理、Frame Completionおよび所有資源のdrain、全Frame CompletionのMain Thread反映、停止前Output WorkerへのFinalize／Abandon要求、Context terminal resultのbounded poll、`Finalized`の局所Registry登録または`Abandoned`予約解放、Output Workerによるowned Access Unit領域解放、native登録解除／Output Buffer／Event／Session破棄、Output Worker停止確認、Main Thread CoordinatorによるUnity管理Texture破棄、全所有資源／予約ゼロ確認、Backend全体の`TryJoin`成功、main-thread terminal intent反映、残存Pendingのtimeout Drop、Trace seal、Disposition=`Finalized`、専用tmp書込み、非上書きPlan rename、既知成功時の`Committed`確定と通常Publication、既知pre-commit失敗時の`Incomplete`とabort cleanup、rename結果不明時の`CommitOutcomeUnknown`とRecovery移行、とする。Submit Worker停止前もOutput Workerは生成済みrecordを消費し、Submit Worker停止後は残存recordだけをdrainする。Accepted件数、record数、Output処理数、Frame Completion生成数およびMain Thread反映数が一致する前にFinalizeせず、各Workerの停止成功後に同Workerへ処理を要求せず、両Worker静止とMain Thread側Texture破棄前にTrace seal／Plan構築へ進まず、Plan rename成功前に`Committed`へ確定しない。Join前にbackend-owned Surface／buffer／owned Access Unit／未確定chunk payloadを外部からDisposeしない。安全な結果／所有権／Service静止を確認できない場合はこの通常停止順へ入れずD-143のprocess-wide Poisonへ進む。

Phase 0の境界確定では将来backendを差し替えられる責務分離だけを確定する。ハードウェアencoder、特定動画codec、profile／bitrate／rate control、packet／GOP／keyframe、PTS／DTS／reorder、container／segment、GPU native handle／zero-copy、独自binary schema、MessagePack／CBOR／Protobuf、worker thread／Job／Burst、queue実測調整はPhase 0の設計・実装・比較・Spikeに含めない。Phase 0.1は既存境界を維持する。Phase 0.11はPhase 0完了後の破壊的変更として、trusted internalな`NvencRunChunkContext`、Registryの単一Run chunk直接登録経路、`FrameSequence` Kindを追加できるが、Phase 0／0.1のPNG Backend、Envelope、Frame／submission Artifact Completion、Publication Planの意味を変更しない。汎用Run Artifact Plugin APIは追加せず、特定codecの生成／verificationとStore実装はBackend内部へ隔離する。

Phase 0.1およびPhase 0.11はPhase 0完了後の独立した後続Phaseとし、Phase 0の完了条件、受入条件、実装範囲を遡及変更しない。Phase 0で使用するPNG＋JSON Backendは両Phaseの追加を理由にPhase 0内で作り直さない。

Phase 0.1はPNG＋JSON Backendを維持し、固定Unity版でthread-safeと規定された現行`ImageConversion.EncodeNativeArrayToPNG`を共有Unity Objectなしでcaller-owned bytesからWorker実行する。`PNG encode -> canonical JSON生成 -> content hash -> staging write -> file flush -> staging renameと必要なdirectory durability -> ArtifactCompletion生成`を固定容量Workerへ移し、Main Thread PNG Fallback、実行時Capability Gate、別PNG library、新しいPNG Format Versionを実装しない。Worker encodeが例外、空出力または不正出力となった場合は同じ入力をMain Threadで再試行せず、既存のCapture失敗終端とTraceへ進んでゲームを継続する。

固定FixtureではWorker出力をdecodeしたRGBA pixelが入力とlossless一致し、寸法、orientation、canonical JSONのproperty順、FrameId／Trace相関が従来経路と一致することを検査する。同じUnity版・同じ現行EncoderでもMain Thread時代のPNG圧縮bytesまたはcontent hashとの一致は要求せず、各出力自身のhashが正しく記録されることだけを要求する。既存Loader／Verifier／Publication／Recoveryが変換なしで同じArtifact形式を受理することを確認し、`durable staging completion`をWorker責務の終端とする。final Publication、`publication.plan`、`capture.index`、Recovery判断、CaptureComplete、cleanupは既存Coordinatorから移さない。Main Thread上の`TryCollectFrameCompletion`／`TryCollectArtifactCompletion`は既存PNG Backendの固定容量Completion Queueを軽量bounded pollし、正式状態遷移へ反映する。Phase 0.1のためにinline Cellへ再実装しない。Phase 0.1は単一の固定容量Worker実行列で成立させ、Encode列とI/O列の分離、payload copy、二重hash、PNG圧縮率の最適化は実測後の後段へ送る。flush待ちによるQueue枯渇ではMain Threadを待たせずCaptureだけをBackpressureまたはDropする。

Phase 0.1／0.11の試験は次の実行階層へ固定する。各試験は正本Tierを1つ持ち、上位Tierは下位Tierのfault位置、deadline、Queue、Registryおよびfile状態の直積を再実行しない。ただし実環境結合の成立を証明するため、Accepted／Completion件数、所有権解放、chunk確定、Plan commit等の最小sentinelを上位Tierでも観測してよい。

| Tier | 実行契機 | 使用可能な環境／入力 | 正本の検査範囲 |
| --- | --- | --- | --- |
| A 通常CI／毎コミット | 全変更 | Editor内でFixtureごとに独立注入するtest `NvencCaptureProcessState`、fake monotonic clock／GPU Fence／NVENC completion／Publication Service、in-memoryまたは小容量temporary Store、数Frame、小synthetic chunk、内部completion gate | bounded予約、Backpressure、通常／制御可能失敗のCompletion／Context終端exactly-once、Source／Input所有権、`Finalize／Abandon -> terminal -> Join`、commit前／後／unknown分岐、Lease、`.partial`非公開、Recovery file-tree分類、Main／Render Thread非待機、代表1件のprocess-wide Poison |
| B 対応環境NVENC統合 | NVENC native plugin、GPU変換、共通Coordinator、Surface所有権、NVENC Profileまたはその依存範囲の変更時、および手動要求時 | standalone Player／専用process、Windows／NVIDIA／D3D11 WDDM、最小の複数Frame列、実NVENC、確定small chunk、Decoder 1 process | Texture受渡し、RGBA→NV12、Accepted FIFO＝submit＝Output回収＝append＝Relation順、固定2 Worker、thread非待機、Surface返却、small chunk decodeだけ |
| C hardware qualification | Phase 0.11承認、Unity／NVENC SDK／native plugin／GPU Driver／GPU／OS／Capture Profile変更、リリース相当判定、明示的な定期qualification | standalone Player／専用process、指定WDDM hardware、120 cadence tick、実時間4秒、確定Run chunk、FFmpeg 1 process、代表Recovery | hardware-qualified nominal、120件のsubmit／Output／Relation順一致、Plan／Publication／CaptureComplete、chunk全体decode、代表的な実process再起動／OS lock Recovery |
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

hardware-qualified `NvencNominalBringUpV1`は全120 tickについて提出試行、Accepted Work Token、encode Frame Completionを厳密に1対1で要求し、Frame Completionは全件`Succeeded, ProducedArtifactCount=0`とする。Run開始から固定2 Workerを並行稼働させ、Submit WorkerがAccepted FIFO順にsubmitして`Submitted` recordを生成する間も、Output Workerは生成済みrecordのOutput／Frame Completionを同順で回収し、Main Threadは完成済みCompletionをbounded pollして8件の固定資源を循環再利用する。StopAccepting後も両Workerは並行drainを続け、Submit Workerは全Accepted recordを生成して先に停止し、Output Workerは残存recordを処理する。全120 Frame CompletionをMain Threadが反映した後だけ停止前のOutput WorkerへFinalizeを要求し、全120 FrameのAccess Unitを同じAccepted FIFO順に含むRun chunk、Context terminalおよび局所Registry登録を確定してからOutput WorkerとBackend全体をjoinする。Accepted FIFO、`NvEncEncodePicture`呼出し、`Submitted` record、Bitstream回収、chunk append、Frame Relation、Frame Completion生成・反映の各Token列が厳密一致すること、chunkのpath、length、hash、TestRunIdと正のCaptureFrameId 120件を重複なく持つことを検査する。Contextの単一`Finalized` Result、局所Registry slot=`Committed`、Disposition=`Committed`、Publication、CaptureCompleteが30秒Finalization期限内に成功しなければPhase 0.11のnominal完了を認めない。この期限はMain ThreadのCompletion反映待ちを含むが、期限超過時にCompletion、terminal resultまたはjoinを捏造しない。受付拒否、Backpressure、Drop、encode／chunk確定失敗、順序違反、件数不足をnominalの許容劣化にしない。この試験は対応hardwareのPhase承認／定期qualificationで実行し、GPUを持たない一般CIのmerge Gateにしない。

fault、Backpressure、Freeze／Drainの各scenarioはTier Aの決定論的試験を正本とし、`FaultCadenceTickCount=16`、`FaultCadenceHz=30`、`FaultSubmissionWindowDeadlineMs=1000`、`FaultFinalizationDeadlineMs=10000`を仮想時刻上限とする。必要tick数をscenario開始前に`1..16`で宣言し、fake monotonic clockのindex `0..DeclaredTickCount-1`を`T0 + index / 30 second`へ同期的に進めて各1回だけ試みる。宣言tick末尾または仮想`T0 + 1000 ms`の早い方で提出を止め、deadline直前では未失敗、1 step超過でscenario失敗となることをsleep／busy waitなしで検査する。事前宣言したFault Injectionと期待Dispositionに一致するAccepted／Frame Completion件数不足またはRun chunk `Abandoned`だけを許し、期待値にない不足、期限後の追加提出、同tick再試行、fault解除後の窓延長は禁止する。仮想10秒超過はscenario失敗と新規Capture処理停止を確定するだけであり、未静止Publication ServiceのSession Leaseを解放する期限ではない。実時間fault、device loss、native hangまたはOS scheduler依存の再実行はTier Dだけとする。

Tier A Recoveryはprocessを再起動せず固定file-tree fixtureを新しいRecovery Coordinatorへ渡し、fake clockで`RecoveryFinalizationDeadlineMs=10000`の直前／1 step超過と期待Dispositionを検査する。確定済みchunkがPlanへ入るfixtureだけPublication／CaptureCompleteを要求し、書込み中chunkだけのfixtureでは`.partial`の無視または破棄とCapture Incompleteを正しい終端とする。Tier Cの代表的な実再起動Recoveryだけは、旧processの提出停止時刻またはmonotonic clockを永続化・継承せず、新processがOS lockを取得して対象RunのRecovery entrypoint `BeginRecovery`へ入った瞬間を`RecoveryT0`として同processのmonotonic clockで固定する。実`RecoveryFinalizationDeadlineMs=10000`以内に期待Dispositionへ到達できなければQualification失敗とし、process停止から`BeginRecovery`到達までの時間は外部Harnessの別診断値とする。実process kill、強制終了、複数外乱、rename結果不明の実OS再現はTier Dへ送り、fault系とRecoveryで30fps持続性能を再証明しない。

`NvencBringUpProfileV1`はWindows 10以降／NVIDIA／D3D11／WDDM、`enableEncodeAsync=1`、SDR／sRGB、左眼、30fps Capture cadence、`width=1280`、`height=720`を固定し、Capture EnvelopeとRun ManifestへProfile IDを保存する。Run開始時にWDDM非同期NVENC capabilityと`CaptureFrameProfile.ImageRect`が厳密に1280×720であることを検査し、TCC、同期modeまたは非同期Completion Eventを使用できない構成をUnsupportedとする。NVENCへ提出する専用RenderTextureはImageRectと同じ全extent、`x=0`、`y=0`、`PixelLayout=RGBA8`、`GraphicsFormat.R8G8B8A8_SRGB`、MSAAなし、mipmapなし、row 0が表示画像上端のtop-left orientationとする。元Textureが別SubRect、Texture Arrayまたはbottom-left orientationなら、提出前のGPU Passでcrop／上下反転とRGBA8 sRGBからBT.709 limited-range NV12への変換を完了し、D3D11 GPU TextureとしてNVENCへ渡す。NVENC Backendは`UnityRenderTextureReadbackDispatcher`その他のPNG用RGBA32 CPU readbackを使用せず、圧縮後のbitstream bytesだけをCPUへ回収する。Backend内ではresize、追加crop、orientation推測を行わずalphaを無視する。1280×720以外の寸法、異なるFormat／Layout／orientation、Dynamic Resolution、ImageRectまたは寸法のRun中変更では新Encoder Sessionへ再構成せず、新規受付を止めてCaptureだけをFail Fastし、ゲームと既存の確定済みArtifactを維持する。

`NvencBringUpProfileV1`は`NvencWorkSlotCount=8`、`NvencEncodeSampleSlotCount=8`、`NvencSourceSurfaceLeaseCapacity=8`、`NvencGpuConversionSyncCapacity=8`、`NvencSubmissionQueueCapacity=8`、`NvencSubmitToOutputQueueCapacity=8`、`NvencFrameCompletionQueueCapacity=8`を固定する。Run開始前にWork Slot領域、Capture専用Source Surface Lease Pool、Encode Sample Pool、GPU変換同期資源および3本の固定Queueを全て一括生成し、どれか1件でも8 in-flight分を確保できなければ取得済み資源を逆順で返してPhase 0.11 CaptureをUnsupportedとする。4組への縮退、可変Pool、実行時resizeまたは別Poolからの借用を禁止する。1件の`NvencEncodeSampleSlot`はNV12 Input Surface、対応するD3D11／NVENC登録handleとmapped所有状態、Output Bitstream Buffer、Completion Event、GPU変換完了証拠およびSlot所有状態を論理的に束ねる。Encode Sample SlotとWork Slotは別Poolであり、Accepted中だけ排他的に1対1で束縛する。Encode SampleはNVENC出力回収と安全なInput／Output／Event解放後に再利用できる一方、Work SlotはFrame Completion回収と全Work所有権解決後まで再利用しないため、両者を同一Slotまたは同一lifecycleとして実装しない。Output Bitstream BufferはNVENC output sampleを表し、`MaxAccessUnitByteLength`と同じ大きさのCPU配列を8本事前確保する契約ではない。既存のbounded Access Unit所有権とchunk append契約を維持し、固定容量計算のoverflow、既知byte領域の総量、native登録および全handle生成をRun開始前に検査する。Submission Queueへの複数呼出元は既存`TrySubmit` admissionの単一線形化境界で直列化し、Submit-to-Output QueueとFrame Completion Queueはそれぞれ単一producer／単一consumerとする。Submit-to-Output Queueのrecordは排他的variantとし、`Submitted`はToken、Encode Sample Slot参照、Input Surface、Output Buffer、Completion Eventを、`FailedBeforeSubmit`はToken、固定失敗／取消Reasonと安全な解放に必要な所有資源情報を持つ。同Queueは各Accepted Workを同順で厳密に1件受け渡す所有権Queueであり、任意順Completionのreorder bufferではない。同時in-flight Accepted Work数は8以下なので、Accepted時に予約したWork Slot、Encode Sample Slotおよび各Queue creditがAccepted後のどちらのvariantにも必要なcapacityを保証し、容量不足でenqueue失敗させない。Source Surface、Encode Sample、Work／Completion領域はPhase 0.11 Capture専用Poolから取得し、Poison時に8組全てを保持してもゲーム描画または他Backendの必須Poolを枯渇させない構成だけを対応する。`ICaptureEvidenceSession.TrySubmit`が受理した時点で、呼出側の`CaptureSurfaceLease`所有権はBackendへ移る。BackendはSource Textureを読むcrop／flip／RGBA→NV12 GPU変換を登録しただけではLeaseを解放しない。GPU queue上で当該変換がSource Textureを参照し終えたことを示す`SourceReadCompleted` Fence／Query等の非同期完了証拠を取得した後だけ、Submit WorkerがLeaseを元Surface Poolへ返却またはDisposeする。これはSource所有権だけの解放であり、この時点では`CaptureFrameCompletion`を発行しない。Main／Render Threadは証拠を待たず、未完了Leaseを元Surface Poolへ返さない。

`NvencCaptureEvidenceBackend`は`MaximumArtifactCountPerSubmission=0`とし、Coordinatorから単一`NvencRunChunkContext`を受け取る。Run開始時にContext、Registry容量1件、writerおよび前項の固定8件資源を一括確保できなければ新規Frameを受け付けず、取得済み資源とRegistry予約を逆順で全返却する。`TrySubmit`はprocess stateが`Running`であり、空Work Slot、Encode Sample Slot、Submission Queue record、Submit-to-Output Queue対応capacity、Frame Completion creditその他必要資源をAccepted前に一括予約できた場合だけ、Work TokenとSurface所有権を固定Submission Queue末尾へ同じ線形化点で移して`Accepted`を返す。容量不足、`NotAccepting`、Poison済みまたは例外では内部予約を残さずSurface所有権を受け取らず無効tokenを返す。Source Surfaceの解放とEncode Sample Slotの再利用は別lifecycleである。Source Read完了後もNV12を保持するEncode Sample Slotは占有を継続し、Output WorkerがNVENC encode完了証拠を取得して圧縮bitstreamの所有権をContextのwriterへ移し、対応Output Buffer／Event／mapped inputを安全に解決した後だけ再利用する。Frame WorkはArtifact Descriptor、content hash、Artifact Completionを生成しない。制御可能な取消、encode失敗、DrainではSource Read完了前のLeaseとNVENC完了前のEncode Sample Slotを早期返却せず安全な証拠後に通常終端する。device loss等で証拠を得られなければ個別失敗終端や隔離解放を行わずprocess-wide Poisonへ進み、Backend全体が8組を所有したままprocess終了へ委ねる。

NVENC submit、Completion／Output Buffer回収、owned Access Unit、chunk append、streaming hash／ByteLength更新、Frame Relation追加、Frame Completion生成は、Run開始から並行稼働する固定2 Worker間のFIFOで直列順序を維持する。Submit WorkerはSubmission Queue先頭のGPU変換が完了するまで後続Workを先にsubmitせず、Accepted FIFO順にだけ`NvEncEncodePicture`を呼ぶ。GPU変換と`NvEncEncodePicture`が成功したWorkは`Submitted`、submit前の検出可能なGPU変換失敗、取消、shutdownまたは`NvEncEncodePicture`失敗は`FailedBeforeSubmit`として、各Accepted Workにつきどちらか一方だけをSubmit-to-Output Queue末尾へ移す。Output WorkerはSubmit Workerのdrain／停止を入口条件にせず、生成済みrecordがあれば同Queue先頭から並行消費する。`Submitted`ではOutput Collectorが対応Completion Eventだけを待ち、Output BufferをlockしてRun開始時に確保済みの16 MiB owned領域へbounded copyし、unlock／unmap完了後にEncode Sample Slot一式を原子的に次Frameへ再利用可能化する。その後、Work Token、CaptureFrameId、owned領域、valid length、返却authorityだけを持つ非copy Leaseを同じOutput Worker上のRun Chunk Sinkへ一方向移譲する。Collectorは移譲後に同領域を読取り・返却・再利用せず、Sinkはaccepted FIFO append、checked ByteLength、streaming hash、Frame Relationを同期更新し、成功または制御可能な失敗でowned領域を厳密に1回返して同期結果を返す。`FailedBeforeSubmit`ではAccess Unitを生成せずNVENC出力を待たずReasonと所有権情報から安全に解放する。Main Thread CoordinatorはRun中も完成済みFrame Completionを更新ごとの固定budgetでbounded pollし、Work SlotとCompletion creditを各再利用条件に従って戻す。安全な結果または所有権を確認できなければrecord、buffer返却、Completionを推測せずprocess-wide Poisonへ進む。Queue record、停止flag、Finalize／Abandon要求およびterminal resultのcross-thread公開はpayload構築後のreleaseと観測側のacquire相当を要求するが、新しいlock-free構造や競合直積を要求しない。後続Eventのsignal状態をprobeせず、Completion callbackの到着時刻、OS scheduler順またはToken fieldから回収順を変更しない。成功Runでは`Accepted FIFO順 = NvEncEncodePicture呼出順 = Completion／Bitstream回収順 = Sink append順 = Frame Relation順 = Frame Completion生成順`、制御可能な失敗Runでは`Accepted FIFO順 = Submit-to-Output record順 = Frame Completion生成順`を不変条件とする。最初の制御可能なcopy／Sink／順序失敗を検出した側は新規受付を停止し、Output WorkerがSink結果を得た時点でContextを`Abandoned`へexactly onceで固定する。SlotがSink結果より先に返るため後続Frameが既にsubmit済みであり得ることを許容する。すでにAccepted済みでまだsubmitしていない残りはSubmit WorkerがNVENCへ新規submitせず`FailedBeforeSubmit(CancelledAfterRunAbandoned)`として同順に渡し、すでに`Submitted`となった残りはOutput WorkerがEvent／Output Bufferを安全に回収するが追加appendせず`CancelledAfterRunAbandoned`として終端する。Contextの早期`Abandoned`確定は残存record、owned Access Unit、Completionまたは資源のdrain完了を意味せず、両Workerは制御可能な収束処理を継続する。Submit Workerは全Accepted record生成後に先に停止し、Output Workerは残存record、owned領域、Sink処理、Completionおよび所有資源を回収し、必要なFinalize／AbandonとContext terminal回収後に停止する。GPU変換、先行NVENC処理または同期Sink I/Oの停止で両Queue／Work Slotが埋まり後続受付が`Backpressured`になる通常のHead-of-line blockingを許容するが、Workerを逐次稼働させる人為的停止、8件ごとのBarrier、Access Unit Queue、追加credit、別I/O Worker、Work Slotへの逆順Bitstream保持、専用reorder満杯Reason、別Session分散、同期FallbackまたはBitstream解析による事後整列を設けない。順序不一致を検出した場合は該当Access Unitを追加appendせずCaptureだけをFail Fastする。

encode、owned Access Unit移譲、Sink append／hash／Relation更新が全て成功したFrameだけを`CaptureFrameCompletion(Status=Succeeded, ProducedArtifactCount=0)`とする。`FailedBeforeSubmit`、copy、encode後、取消、null／空出力、Access Unit上限、chunk上限、順序、append、Relation／counter更新または先行失敗後の残存Workでは、Sink／Collectorの同期結果後にOutput Workerが`Failed`または`Cancelled, ProducedArtifactCount=0`を予約済みFrame Completion creditへexactly once公開してContextを`Abandoned`へ進める。部分write後の失敗をtruncate、hash rollbackまたはRelation修復せず未確定chunk全体を破棄する。Frame Completionの唯一のproducerはOutput Worker、consumerはMain Threadとし、Output Collector、Run Chunk Sink、Submit Worker、GPU callbackまたはCoordinatorはCompletion Queueへ直接書かない。Frame Completionには固定配列SPSC Queueを使う。各Queue操作は短いlockまたは同等のrelease／acquire同期を許可し、consumerはlock取得を待ち続けず取得できなければ次回pollへ送る。Accepted前に全capacityとcreditを予約するため、Accepted後のSubmit-to-Output recordまたはCompletion enqueueは容量不足で失敗しない。16 MiB owned領域はOutput Worker専用にRun開始時確保するため新しいper-Submission creditを持たず、確保不能なら受付開始前にUnsupportedとする。通常／制御可能な失敗経路のWork Slotは対応Completionが回収され、全Source／Input／owned Access Unit所有権が返却済みになった後だけ再利用する。Poison経路ではSink所有中のowned領域も返却せず、Completionと資源ゼロへ収束させず、Backend全体が全slot／credit／handleを所有したまま再利用不能となる。Phase 0.1 PNG Queue実装そのものはcross-thread転用せず変更しない。一般thread pool、work stealing、lock-free Cell、fieldごとのbarrier、読取り前後のgeneration二重照合または全timingの競合網羅をPhase 0.11の成果物にしない。

Accepted後のFrame Completion exactly-onceは、Backendが成功、検出可能な失敗、取消または制御可能なshutdownのterminal処理へ到達した場合だけの安全契約である。native NVENC callの診断期限超過、Completion EventとWorkの対応不明、device loss後のSource／Input／Output利用終了不明、Output Buffer ownership不明、OS I/O／rename call未帰還またはService静止不明等、安全な結果または資源状態を確認できない制御喪失では、Composition Rootの単一process stateを`PoisonedUntilProcessRestart`へ一方向に進める。診断期限超過は永久停止の証明ではなく、安全側にPoisonするprocess-local判断である。Poison stateは永続Schema、Trace終端、Artifact、PlanまたはRecovery stateへ追加しない。

Poison後は新規`TrySubmit`、chunk追加append、streaming hash／ByteLength確定、Frame Completion、`Finalized`／`Abandoned`、Trace Freeze／Summary、正式なIncomplete、Plan commit、Publication、CaptureComplete、同process RecoveryおよびPhase 0.11 Backend再生成を行わない。Work Slot、Completion credit、`CaptureSurfaceLease`、Input／Output Surface、Completion Event、native／file handle、Queue record、Session Ownership LeaseおよびOS lockを個別に回復、通常Poolへ返却または別Runへ移譲せず、PoisonされたBackend／Coordinator／Serviceがprocess終了まで一括所有する。資源別`Quarantined`状態、Quarantine台帳、容量回復、特殊な資源ゼロ判定を設けない。Poison前に確定・公開済みの別Run／Artifactはrollbackしない。

blocking native／OS境界がPoison後に帰還した場合、Worker／Serviceは副作用前に同じprocess stateを確認し、Completion、append、Relation、Context終端、Plan、通常Pool返却、OS lock解放またはCapture再開を行わない。安全に閉じられるprocess-local wrapperのbest-effort closeは実装裁量とするが、正常Drainへの復帰、Join成功、exactly-once再収束または資源ゼロをProduction契約にしない。process終了後はOSによるhandle／lock解放に委ね、新processがPoison状態を復元せず、既存の完成Planとfile集合だけをauthorityとしてRecoveryする。

`FinalizationDeadlineMs=30000`はPhase試験とCaptureを期限超過として失敗判定し、process-wide Poisonへ進める診断期限であり、永久停止を証明したりFrame Completion、資源解放、成功JoinまたはOS lock解放を捏造・強制する期限ではない。制御喪失後はTrace Freeze、Plan commit、`Incomplete`を含むRun終端または成功Joinへ進めない。Production公開APIを増やさず、内部テスト構成だけがNVENC呼出し直前または完了回収境界へfake native completion gateを注入できるようにする。Capture subsystemはMain／Render Threadを待たせずゲーム停止を要求しないが、共有D3D11 Device／GPU Driver自体のdevice loss後もUnity描画が継続することは保証しない。

Main ThreadとRender ThreadではNVENC Completion、GPU Fence、Texture空き、bitstream lock／取得、hash、file write、close／renameを待たない。Render Thread／Native Plugin callbackはcrop／flip／色変換を行うGPU workと固定Submission recordに必要なbounded command／handle登録だけを行って戻り、`NvEncEncodePicture`自体はAccepted FIFOを所有するSubmit Workerだけが呼ぶ。callback内でblocking encode、poll loop、sleep、device-context待機またはI/Oを行わない。GPU完了証拠のpollとNVENC submitはSubmit Worker、Completion Event待機、bitstream取得、content hash、chunk staging append／finalizationはOutput Workerが行う。1件のAcceptedに必要なWork Slot、Encode Sample Slot、Source Surface／GPU変換同期資源、各Queue recordおよびFrame Completion creditのいずれかを1件分でも一括予約できない場合だけ、Surface所有権を移転せずMain／Render Threadを待たせず`Backpressured`を返す。各資源に空きがある限り1～8件目を受理でき、全capacityが予約済みで必要資源を確保できない9件目を拒否する。8組は短時間jitterを吸収し得る固定余裕に限り、`8 / 30 second`または追加4組分の`4 / 30 second`を停止吸収保証として扱わない。nominalでBackpressureが1件でも発生すればQualification失敗とし、容量の自動拡張、4組への縮退、追加Workerまたは同Phase内の再調整を行わない。専用fault scenarioではBackpressureを期待Dispositionとして検査する。

crop、orientation補正、RGBA→NV12は可能な範囲で同一のversion付きGPU conversion passへまとめ、CPU roundtripを禁止する。API／format制約で追加GPU copyが必要な場合はPass数、copy数、Texture遷移を固定ProfileとProfilerへ記録するが、zero-copy方式比較や複数実装の最適化をPhase 0.11へ持ち込まない。構造テストでは`NvencCaptureEvidenceBackend`が`UnityRenderTextureReadbackDispatcher`を参照しないこと、Main／Render Thread経路から待機可能APIとArtifact Storeを呼ばないことを検査し、ProfilerでNative callback時間、GPU conversion pass時間、Source Surface／Input Slot占有、Backpressure数を診断値として取得する。これらの時間へ新しい合格閾値または性能SLAを設けない。Tier Aの必須回帰は小payloadと数Frameを使う次の10代表caseに限定する。(1)正常な数Frameが1 chunkとして確定、Trace Freeze、Plan commit、Publicationされる、(2)pre-finalize失敗でPlanを作らず`Incomplete`となる、(3)chunk Finalize後かつrename呼出し前の失敗で局所Registry slotと予約を解放し`Incomplete`となる、(4)NVENC専用tmpだけのfixtureを自動昇格しない、(5)完成Planだけのfixtureを通常Recoveryできる、(6)完成PlanとNVENC専用tmpの同時存在をcollisionとして無変更停止する、(7)commit後のPublication失敗でPlan／chunkを削除せず`PublicationRecoveryRequired`となる、(8)abort cleanupのfile削除失敗でもゲームを止めずLeaseを解放する、(9)同一Runで局所Registry登録とPlan commitを重複実行しない、(10)Main／Render ThreadからPlan I/O、cleanup、待機可能API、hashまたはfile I/Oを呼ばない。`CommitOutcomeUnknown`はfake Publication Serviceのrename結果不明で作り、その場で再読込・cleanupしないことを1件だけ検査する。容量不足、Source／Input遅延、Completion、Context終端、Session Lease、commit前cleanup、commit後非cleanup、unknown非cleanupおよび`.partial`非公開は各不変条件につき共通unit test 1件へ集約し、fault位置と状態の直積を作らない。成功時は各Frameが`Succeeded/count=0`を厳密に1件返し、全Frame回収後かつOutput Worker Join前だけContextが`Finalized` Resultを1件生成する。制御可能な失敗／取消Frameまたは0 Accepted Frameでは`Abandoned`として予約を1回だけ解放し、Resultを生成しない。Frameが全てStagedでもContextが`Abandoned`または局所Registry未登録ならArtifact 0件のPlanを構築せずCaptureCompleteを発行しない。通常／制御可能な失敗caseだけで資源ゼロを要求する。Tier Aのordered pipeline試験はGPU変換ready順をAccepted FIFOと意図的に異ならせてもSubmit Workerが先頭Workだけを先にsubmitし、Submit-to-Output QueueとOutput Workerが同順を維持することを検査する。後続Eventの先行signalを観測・回収するfake、逆順Bitstream保持またはreorder結果検査は作らない。fakeで順序recordを破損した場合は追加appendせず`Abandoned`／Capture Fail Fastとなることを1件だけ検査する。複数Run Artifactまたは2番目のchunkはPhase 0.11の構造上生成できず、Phase 4.8の別versionへ送る。

Tier Aではsubmit前GPU変換失敗と`NvEncEncodePicture`失敗を各1件注入し、失敗したAccepted Workが`FailedBeforeSubmit`として同じ予約済みFIFO slotへ渡され、Output Workerだけが対応するFailed Completionを厳密に1件発行することを検査する。先行する`Submitted`、失敗record、すでにAccepted済みの後続Workを混在させ、後続が`FailedBeforeSubmit(CancelledAfterRunAbandoned)`または回収済み`Submitted`からのCancelled Completionへ順番に収束し、追加chunk append、二重Completion、Work／Completion credit漏れ、Source／Input早期再利用がないことを確認する。

Tier Aの固定容量回帰は、8件の異なるfake caller-owned Source Surface Lease、8件のGPU変換同期credit、Work Slot、Encode Sample Slotおよび全Queue／Frame Completion creditを各Accepted前に一括予約し、予約カウンタと各Lease／creditの所有者が8件全てで対応するWorkへ移転済みであることを同じ単一fixtureで検査する。いずれも解放しない状態の9件目は、これらを含むAccepted 1件分の全資源を取得できず、Source Surface Leaseその他の所有権を一切移転せず非待機の`Backpressured`となることを確認する。各Workの`SourceReadCompleted`証拠を個別に公開した後だけ、そのWorkに対応するSource Surface LeaseとGPU変換同期creditが再利用可能となり、他の7件、Encode Sample SlotおよびWork Slotは各自の後段の再利用条件まで占有を継続する。続いてOutput回収、owned領域へのcopy、Bitstream unlock、mapped Input解除を進め、Output Buffer／Event／registered handle自体を破棄・登録解除せず、対応するEncode Sample Slot一式だけがSink完了前でも次Frameに再利用可能になることを確認する。Work SlotとCompletion creditはSink結果、owned領域返却、Frame Completion回収まで占有を継続する。GPU ready順をAccepted FIFOと異ならせても8組をreorder bufferとして使わず、submit／Output／Sink append／Frame Relation／Completion順が変わらないことを既存ordered pipeline試験へ統合する。4／6／8／12組の比較matrixは作らない。Run初期化の固定caseとして、Capture専用Source Surface Poolを8件一括確保できない場合、GPU変換同期資源を8件一括確保できない場合、または16 MiB owned領域1件を確保できない場合を各1件だけ検査し、いずれも取得済み資源を逆順で返して4件／小領域へ縮退せずUnsupportedとなることを確認する。Tier Bは短い実native経路、Tier Cは120 Frame nominalで固定8組とowned領域を一括初期化でき、Backpressureなしで完了することだけを追加確認する。既知固定領域またはnative handleのその他の一括確保失敗も縮退・実行時再確保せずUnsupported、Poisonは8組とowned領域を通常Poolへ返さず別Fixtureの隔離stateだけがRunningで開始できることを検査する。

Tier AのOwned Access Unit／Chunk Sink回帰は既存Output回収fixtureへ成功1件とappend失敗1件だけを統合する。copy／unlock／unmap完了後にEncode Sample SlotがSink結果前でも返却済みであること、Sink移譲後にCollectorからLeaseを読取り・返却できないこと、成功と制御可能なappend失敗の双方でowned領域が厳密に1回返ること、同じLeaseの二重append／二重返却を副作用前に拒否することを確認する。SinkはFrame Completion Queueへ書かず、Output Workerだけが同期Sink結果後にCompletionを生成する。append失敗では後続submit済みWorkを既存`CancelledAfterRunAbandoned`へdrainし、部分chunkを修復しない。通常Drainでは未返却／所有者不明Access Unitが0件、PoisonではSink所有領域を返却せず成功Joinへ進まないことを検査する。第三Worker、Access Unit Queue、実I/O stall、OS scheduler、長時間SSDまたはfault直積を追加しない。

Tier Bの短いinterop確認は、Run開始時に8組のNV12 Texture／Output Buffer／EventとInput／async-event登録が各Slotにつき厳密に1回成功し、短い複数Frame列で同じ8組を循環再利用し、正常終了時に各native登録解除、Output Buffer／Event／Session破棄が厳密に1回行われることをnative call counterで検査する。Unity側instrumentationではWorker停止証拠後のMain Thread Texture破棄が各1回であることを確認する。Frame処理中のTexture／Buffer／Event生成・破棄、native pointer再取得、register／unregister、Session再初期化が0回であること、Main／Render ThreadにGPU Fence／Completion Event／`NvEncLockBitstream`待機がないこと、短いFrame列がBackpressureなしで完了することを確認する。Render callback時間とGPU conversion pass時間をProfiler診断値として記録するが、閾値、P50／P95／P99、長時間録画、複数GPU／Driver比較または新しい性能SLAを追加しない。

Tier Aのinterop終了順回帰はthread-affinityを記録するfake Textureとnative call counterを使い、Output Workerが全unlock／unmap、登録解除、Output Buffer／Event／Session破棄を完了して停止するまでMain Thread側Texture破棄が0件であること、Worker停止証拠後にMain Threadだけが8 Textureを各1回破棄すること、その完了前はBackend全体`TryJoin()`と資源ゼロが成立しないことを検査する。Output WorkerまたはSubmit WorkerからのUnity Texture破棄、Main Threadからのnative unregister／Session破棄、二重破棄を副作用前に拒否する。Poison fixtureでは両側の破棄counterが0のまま全資源を保持し、成功Joinへ進まないことを確認する。この1 fixtureを既存の停止順回帰へ統合し、新しいthread競合matrixを作らない。

Tier AのWorker並行動作／停止順回帰は12件のfake Workと仮想stepだけを使う。先行Workからrecordを生成した時点でSubmit Workerを停止せず、Output Workerが生成済みrecordを並行消費し、Main ThreadがCompletionを反映して対応する固定資源を戻した後に9～12件目もAcceptedできることを検査する。Queue容量を12または120へ増やさず、8件ごとの停止／再開Barrierを設けない。StopAccepting後も両Workerを並行stepし、Submit Worker停止時点では全Accepted recordが各1件生成済みかつOutput Workerは未停止であること、同Workerが残存recordをdrainすること、全Frame CompletionのMain Thread反映前またはSubmit-to-Output Queue drain前にはFinalize／Abandon要求を受理しないこと、Context terminal result回収前にはOutput Workerを停止しないこと、両Worker静止前にはTrace seal／Plan commitへ進まないことを確認する。Output Worker停止後の追加Finalize要求も副作用前に拒否する。共通`TryJoin()`はこの最終状態だけでtrueとなり、Submit Worker単独の停止確認には使わない。実時間待機、実NVENC、OS scheduler依存またはtiming競合matrixを追加しない。

Tier AのPoison試験はFixture専用process stateとfake native completion gateを使う代表1件だけを必須とする。Accepted Workを持つ状態でgateを停止しfake診断期限を越えたら`PoisonedUntilProcessRestart`へ一方向遷移し、新規`TrySubmit`拒否、偽Completion／Context terminal／append／Plan／Publication／CaptureCompleteなし、credit／GPU資源／OS lock再利用・解放なし、Main／Render Thread非待機を確認する。gateを後から解放しても`Running`／`Draining`へ戻らず、Completion、append、Capture再開、Plan commit、同process Recovery、通常Joinまたは資源ゼロへ進まないことを確認する。Poison Fixtureの破棄後に別Fixtureの独立stateが`Running`で開始できること、Production Composition Rootにはstateが1個だけでReset／Unpoison入口がないことを構造検査する。native帰還後のexactly-once再収束、Drain／Join再開、資源別Quarantine／回復matrix、停止位置とFreeze／Cancel／Abortの競合直積を要求しない。実native永久停止、device loss、Driver hangおよびprocess killはTier Dだけで扱う。

Tier AのPlan境界回帰は小さい固定file-tree fixtureを使い、Phase 0.11のpre-commit失敗後に`publication.plan.nvenc-precommit.tmp`削除も失敗した状態を入力して、canonical bytes、正しいRun相関および対応chunkが存在しても`publication.plan`へ昇格せず隔離／破棄されることを確認する。同じsuiteでPhase 0／0.1のcanonical `publication.plan.tmp`だけが残るfixtureは従来どおり昇格できることを確認する。fake Publication Serviceのrename例外／結果不明は`CommitOutcomeUnknown`へ固定し、final／tmpの再読込、Registry slot破棄、staging削除または再renameを行わない。Service request実行中、rename call未帰還またはterminal result未回収ではSession Leaseを解放できず、terminal result後はService-owned handle、request slot、bufferおよびI/O commandがすべてゼロであることを検査する。完成Planのみ／専用tmpのみ／Planなし／final＋tmp／finalとchunk不一致をそれぞれ`PublicationRecoveryRequired`／Incomplete-orphan cleanup／Incomplete-orphan cleanup／`PublicationRecoveryCollision`／Recovery collisionへ分類する。完成Plan＋専用tmp fixtureはsentinelの存在、長さ、content hashが検査前後で不変で、Plan／Artifact公開とCaptureCompleteが発生しないことを確認する。実process再起動とOS lock解放の代表経路はTier C 1系統、timeout／取消中の実Worker、実rename結果不明または外部改変はTier Dへ分離する。

Run開始時はWDDM modeとNVENC capability queryでH.264 High Profile、NV12入力、固定寸法、必要なD3D11／非同期Encode機能を問い合わせ、`enableEncodeAsync=1`、`enableOutputInVidmem=0`と要求設定による単一Encoder Session初期化の成功を必須とする。Capture開始前の非クリティカルな準備区間で固定8組のCapture所有NV12 Input Texture、Output Bitstream Buffer、Completion Eventを生成し、各Textureのnative pointerを1回だけ取得して`NvEncRegisterResource`、各Eventを`NvEncRegisterAsyncEvent`で1回だけ登録し、返却handleを対応Slotへ保存する。Unity管理資源の生成・破棄がMain／Render Threadを要求する場合もゲーム中のFrame処理へ混入させず、開始／終了境界の限定操作とする。全8組とSessionの初期化が成功した後だけSubmission受付を開始する。途中失敗では使用開始前の取得済みEvent登録、Input登録、Output Buffer、Event、Texture、Sessionを安全な逆順で各1回rollbackし、4組への縮退、実行時再生成または別方式Fallbackを行わずCaptureだけをUnsupportedとする。TCC、同期mode、Completion Event登録不能または固定2 Workerのordered async経路を構築できない場合もUnsupportedとする。アプリ内でAnnex AのMaxFS／MaxMBPS／MaxBR／MaxCPBを再計算せず、対応可否はcapability queryと初期化結果を正本とする。RGBA8 sRGBからBT.709 limited-range NV12への変換はversion付き固定GPU Passで行うが、係数、量子化丸め、chroma sample location、H.264 VUIの個別bit値をPhase 0.11のProduction受入契約にしない。変換Profile IDとVersionだけをRun Manifestへ記録し、色とorientationの妥当性は後述のDecoder結合テストで確認する。

Encoderへの要求設定はH.264 High Profile、progressive Annex B、各入力をIDRとしてencode、SPS／PPS反復、P／B Frameなし、Frame間参照なし、Constant QP 28、`enableEncodeAsync=1`、`enableOutputInVidmem=0`とし、Output Workerの`NV_ENC_LOCK_BITSTREAM::doNotWait=0`を固定する。SDK／Driver識別子と全要求値をRun Manifestへ記録する。`NvEncLockBitstream`は対応Completion Eventの待機後にOutput Workerだけが呼び、Main Thread、Render Thread、Submit Workerから呼ばない。AUDの有無はPhase 0.11の受入条件にしない。NVENC APIが要求設定またはSession初期化を拒否した場合はCaptureだけをFail Fastする。ProductionはNVENC出力を再解析して設定適合を証明せず、NVENCが返したraw Annex B bytesを正本とする。

各Frameの通常経路は、既存Source Textureから予約済みNV12 Input Textureへの固定GPU変換、`NvEncMapInputResource`、`NvEncEncodePicture`、Output WorkerでのCompletion Event待機、`NvEncLockBitstream(doNotWait=0)`、Run開始時確保済みowned領域へのbounded copy、`NvEncUnlockBitstream`、`NvEncUnmapInputResource`、Encode Sample Slot一括返却、同Worker上の同期Run Chunk Sinkへの一方向移譲、append／hash／ByteLength／Frame Relation、owned領域返却、Sink結果後のFrame Completion生成だけに限定する。Run中はCapture BackendによるNV12／補助RenderTexture、Output Bitstream Buffer、Completion EventまたはEncoder Sessionの生成・破棄、`NvEncRegisterResource`／`NvEncUnregisterResource`、`NvEncRegisterAsyncEvent`／`NvEncUnregisterAsyncEvent`、native Texture pointer再取得、Pool resizeまたはSession再初期化を禁止する。Texture、pointer、registered handleはRun中不変とし、Texture再生成、Graphics Device reset、device loss、handle不整合または既存登録を安全に利用できない状態では、その場で再登録せず制御可能ならCapture Fail Fast、所有権証拠が不明ならprocess-wide Poisonへ進む。`doNotWait=0`がEvent待機後も帰還しない場合もCompletionを捏造せずPoisonとし、同processでは録画を復旧しない。Chunk SinkはOutput Worker上の同期内部呼出しであり、独自Worker、Queue、lifecycle、Drain、JoinまたはCompletionを持たない。

Phase 0.11の永続映像Artifactはappend-onlyの`ArtifactKind=FrameSequence(6)`、`FormatId=NvencH264IdrChunk`、`FormatVersion=1`とする。`FrameSequence`はcodec非依存の複数Frame共有Artifactを表し、既存Kind 0～5を再番号しない。nominal Runはchunk sequence 0だけを使い、staging rootとfinal rootのrelative pathを`chunks/chunk-0.nvenc-idr-chunk-v1.h264`、未確定pathを`chunks/chunk-0.nvenc-idr-chunk-v1.h264.partial`へ固定する。`.partial`はDescriptor、Artifact Completion、Plan、Capture Indexへ登録しない。確定chunkの`CaptureArtifactFrameRelation`は、そのchunkへappendを完了した正のCaptureFrameIdをaccepted順に重複なく保持する。Work TokenはFrame encodeのprovenance、Context ownershipはchunk生成の内部provenance、Frame Relationは意味上の関連であり相互に代用しない。Phase 0.11は各Frameのbyte offset、random access、seek indexをProduction契約にしない。

各Accepted Work Tokenについてencode API成功、返却bufferが非nullかつ1～`MaxAccessUnitByteLength=16 MiB`であることだけをAccess Unitのbyte受入条件とする。Output Worker専用の16 MiB owned領域をRun開始時に厳密に1件確保し、確保不能なら巨大配列、可変PoolまたはAccepted後確保へFallbackせず受付開始前にUnsupportedとする。同Worker上の同期Sinkにより同時にSink所有となるAccess Unitは最大1件であり、新しいQueueまたはBackpressure creditを設けない。NVENCが返した3／4-byte start code、NAL順、leading／trailing zeroを変更せず、accepted CaptureFrameId順に`.partial`へstreaming appendする。Run chunkのchecked累積上限を`MaxChunkByteLength=256 MiB`とし、上限を越えるAccess Unitは部分appendせずchunkを`Abandoned`へ送る。Frameごとのcontent hash、flush、close、rename、Descriptor、Artifact Completionを生成しない。Sinkの成功／制御可能な失敗ではowned領域を厳密に1回返すが、所有権不明またはPoisonでは返却しない。`.partial`の所有権はRun chunk writerが保持し、部分write、Relationまたはcounter失敗でもtruncate／rollback／修復せずchunk全体を`Abandoned`にする。

正常Freezeの開始はTrace FreezeではなくStopAccepting／`BeginDrain`である。Coordinatorはその線形化点でAccepted件数Snapshotを固定し、Submission Queueへの追加だけを止める。Submit WorkerとOutput Workerは並行動作を続け、Submit WorkerはSnapshot内の全Accepted Workについて`Submitted`／`FailedBeforeSubmit` recordを各1件生成してrecord producerとして先に停止する。この停止は、以後record追加とNVENC新規submitがないことだけを証明し、Output drain、NVENC Completion回収、Sink処理、Frame Completion生成・反映、chunk FinalizeまたはBackend全体joinを証明しない。Output WorkerはSubmit Worker停止前から生成済みrecordを処理し、停止後は残存record、owned Access Unit、同期Sink処理と所有資源をdrainする。Main Threadは全Frame Completionをbounded pollして正式状態へ反映する。この時点ではOutput Workerを停止せず、Run chunkを`Finalized`にできる最小条件として、(1) AcceptedかつSucceededのFrameが1件以上、(2) Access Unit append件数が1件以上、(3) Accepted件数Snapshot、record生成、Output回収、Sink結果、Succeeded Frame、append済みAccess Unit、Frame Relation、Frame Completion生成・Main Thread反映の件数と順序が一致すること、(4) 通常経路の未返却owned領域／所有者不明Access Unitが0件、(5) 全Accepted FrameがSucceededで`FailedBeforeSubmit`が0件であること、を検査する。0 Accepted Frame、0 Access Unit、件数／順序不一致、未返却owned領域、`FailedBeforeSubmit` 1件以上またはいずれかのFrame失敗では空chunkを確定せず停止前のOutput Worker上で`Abandoned`へ進む。所有者不明なら`Abandoned`へ推測収束せずPoisonする。

最小条件を満たした場合だけ、Coordinatorは停止前のOutput WorkerへContextのFinalizeを要求する。Output Workerは同じappend直列化境界でappendを停止し、appendごとに更新済みのchecked累積ByteLengthと単一streaming content hashを確定してhandleをcloseし、確定staging pathへ非上書きrenameした後だけ、Context内部状態からDescriptor／Relation／pathを持つ`NvencChunkFinalizationResult`を構築して`Finalized`を公開する。hashのためにfileを再読込せず、`Flush(true)`を呼ばず、close後にhandleを再openしない。Main Threadの`NvencCaptureRunCoordinator`はterminal resultをbounded pollし、自身が所有するContextとResultを局所Registry slotへexactly once登録する。`Abandoned`では全Accepted record／Completion／owned Access Unit／Sink処理／資源drainと予約解放を完了する。Context terminal回収と登録または予約解放が終わり、全native call帰還、全Inputのunmap、全Bitstream Bufferのunlock、通常経路のowned領域返却、GPU／NVENC非使用を確認した正常／制御可能経路だけで、Output Workerは16 MiB owned領域を解放し、最後のnative teardownとして8組の`NvEncUnregisterAsyncEvent`、`NvEncUnregisterResource`、Output Bitstream Buffer／EventおよびEncoder Session破棄を依存関係の逆順で各1回行い、Unity管理NV12 Textureへ触れずに停止する。解放途中の失敗で安全な所有権が不明になった場合は残りを推測解放せずPoisonする。Output Worker停止証拠とnative teardown完了証拠を取得したMain Thread Coordinatorは、非クリティカルな終了区間でUnity管理NV12 Textureを各1回破棄する。Main Thread側破棄完了後だけBackend全体の資源ゼロを成立させ、停止済みWorkerへFinalize、Abandon、unregister、破棄またはI/Oを要求しない。全Frame CompletionのMain Thread反映前のFinalize要求またはResult登録を禁止する。共通`ICaptureEvidenceSession.TryJoin()`はSubmit WorkerとOutput Workerの静止、owned領域解放、native teardown、Main Thread側Texture破棄および全Backend資源ゼロを確認した最終状態だけでtrueを返す。Poisonではowned領域返却／解放、native unregister／破棄またはUnity Texture破棄を行わず、資源ゼロまたは成功Joinを要求せず、登録済み8組、Sessionとowned領域をprocess終了まで一括保持する。両Worker静止、資源ゼロ確認およびTrace sealまで成功した候補はDisposition=`Finalized`へ進み、Session Ownership Leaseを保持したまま非所有Publication ServiceへPlan書込み／renameを要求する。rename既知成功時に局所slotとDispositionを`Committed`へ進め、通常Publicationへ進む。別CoordinatorへのContext、Lease、Freeze ReceiptまたはRegistry authorityの移譲を行わない。

encode、append、hash、close、Result生成、局所Registry登録、Join、資源ゼロ確認、Trace sealまたはrename呼出し前のPlan書込みの失敗・取消では明示Abortとし、局所slotが`Registered`なら同じCoordinatorが`Empty`へ戻して予約を解決した後に`Incomplete`へ確定する。Contextが`Abandoned`でもJoinと資源解放を完了してTrace sealできる場合はTraceをFrozen化した後に`Incomplete`へ確定する。成功候補のTrace sealが失敗した場合は再試行するか、明示Abortして同じIncomplete経路へ進む。rename既知成功後の失敗では局所slot=`Committed`とDisposition=`Committed`を維持し、Plan／chunkを取消・削除せず次回Recoveryへ送る。rename成否不明では`CommitOutcomeUnknown`へ進み、その場でfile集合を検査または変更しない。Finalize前の失敗ではContextを`Abandoned`へexactly onceで固定し、Finalize後の失敗ではContextを`Finalized`のまま維持する。pre-commit失敗経路ではPlan、Capture IndexまたはCaptureCompleteを生成せず、ゲーム、既に確定した別Artifact、既に公開したFrame Completionをrollbackしない。したがってFrame Completion成功は当該Frameのencode／append成功、Disposition=`Committed`だけが同processでchunkの通常Publicationへ進めることを表す。Trace enqueue／sealまたはpre-commit失敗はRun Incompleteになり得るがFrame CompletionまたはContext outcomeを再発行せず、Frozen Traceへ事後理由を追記しない。

RecoveryはProduction H.264 parserを持たず、Planへ登録済みの確定chunkについてDescriptorのcanonical path、ByteLength、content hash、TestRunId、ArtifactId、Frame RelationをPublication／Recovery Worker上の固定memory streaming verificationで再検査する。process-localなwriter hash、`NvencChunkFinalizationResult`または旧processのPublish Receiptを再利用せず、Artifact全長配列を確保しない。同じOS lock下の単一Recovery分類で得た検証結果を後続実行へ渡す既存Snapshotで安全に再利用できる場合は同じfileを理由なく再hashしないが、そのための新しいToken／owner generation／汎用authority体系を追加しない。bufferを取得できない試行は`Deferred`としてService terminal回収後にLeaseを解放し、Plan、chunk、staging、finalまたは専用tmpを検査済みとも不一致とも分類せず、そのfile集合を無変更のまま後続Recoveryへ残す。保存済み確定bytesのlength／hash不一致、path／Run／Frame相関不一致では対象Artifactを変更せずRecovery collisionとして停止する。`.partial`、Planに未登録のstaging chunk、確定rename前に停止したchunkは未確定であり、OS lock下で安全に無視または全体を破棄する。NAL走査、安全位置へのtruncate、末尾救済、部分hash、部分Relation復元、部分公開を一切行わない。未確定chunk喪失は当該CaptureをIncompleteにするが、ゲームRun全体または既に確定した別Artifactの破損とは扱わない。Recovery時にstart code、SPS／PPS、Slice、Level、VUIを解析せず、raw bytesをcanonicalize／再serializeしない。OS crash／電源断に対する完全durabilityをPhase 0.11で保証しない。

Phase 0.11 RecoveryのPlan authorityは完成名`publication.plan`だけとする。新processは旧processの`CommitOutcomeUnknown`その他process-localな`NvencRunEvidenceDisposition`を復元・推定せず、OS lock取得後のfile集合から次の順で分類する。(1)完成Planだけが存在しcanonical検証、Run相関および対応chunkのlength／hash一致に合格すれば`PublicationRecoveryRequired`、(2)NVENC専用tmpだけ、または完成Planも専用tmpもないPlanなしrootはIncomplete／orphan cleanup、(3)完成PlanとNVENC専用tmpが同時に存在すれば`PublicationRecoveryCollision`、(4)完成Planがあっても対応chunkが欠落または不一致ならRecovery collision、とする。`publication.plan.nvenc-precommit.tmp`だけが残るrootは、明示Abort、pre-commit失敗またはcrashのいずれであるかを推定せず、同tmpをPlanとしてparse／昇格／部分利用しない。OS lock、no-follow、固定path、Run root相関を検証して隔離または全体破棄し、削除失敗時も自動公開せずIncomplete rootとして残す。完成PlanとNVENC専用tmpが同時に存在する場合は、どちらの内容がcanonicalまたは相互一致するかにかかわらずfail closedに停止し、final、tmp、chunkまたは他のRun root内fileを削除、rename、上書き、公開せず、CaptureCompleteを発行しない。Phase 0／0.1の`publication.plan.tmp`昇格規則へこの制限を波及させない。

Tier B／CのDecoder結合テストはProduction Captureとは独立した`NvencChunkDecodeSmokeProfileV1`を使い、Tier AではFFmpegその他の外部processを起動しない。DecoderはFFmpeg 9.0.1の固定Portable Bundleとし、実行file／packageのSHA-256、起動option、OS、NVENC SDK、GPU、Driverをテスト結果へ記録する。Bundle treeのcanonical index、環境変数schema、DLL探索の再実装、append-only外部Tool Profile基盤をPhase 0.11の成果物にしない。Test Runnerは検証済み絶対pathのDecoderを使い、確定Run chunk 1件につき新しいprocess／sessionと空の作業directoryを1つだけ作る。raw Annex B chunkだけを入力し、外部extradata、別Run Artifact、Decoder session／cacheを再利用しない。Tier Bは実native経路と複数Framechunkを証明できる最小Frame列だけ、Tier Cはnominal 120 Frame chunkだけを入力する。timeout、stdout／stderrのbounded回収、失敗時のprocess tree停止と作業directory回収はTest Runnerの安全要件として維持するが、これらをProduction schemaへ追加しない。

Tier C nominalの確定chunkをclean Decoder process 1回で先頭から末尾まで逐次decodeし、出力Frame数がFrame Relation件数と厳密に一致して120であること、全Frameの寸法が1280×720であることをstreaming count／metadataで確認する。全120 FrameのRGBAを同時保持または個別file保存せず、比較対象以外は検査後ただちに破棄する。Frame Relation列のordinal `i`とdecode出力ordinal `i`を対応させ、先頭、中央、末尾のFrame markerだけをテスト専用expected sequenceと照合する。Productionは各Frameのbyte offset、seekまたはrandom decodeを保証せず、Smoke Testもそれらを要求しない。確定chunkのpath、length、hash、Run相関とFrame Relationは全件検査するが、未確定chunkをDecoderへ入力しない。

色とorientationのSmoke Testは1280×720固定の`NvencDecodeFixtureV1`を使う。Fixtureは異なる安全色の4領域と左上だけの非対称markerを持ち、上下／左右反転または90度回転を識別できることを必須とする。decode列の先頭、中央`floor((N-1)/2)`、末尾Frameだけを画像比較対象とし、各領域の境界から8 pixelを除外した平均RGB絶対誤差が各channel 24 code value以下で、markerが左上ROIに存在すれば成功とする。P99、gradient精度、詳細な色変換品質、PSNR／SSIMはPhase 0.11の完了条件にせず、必要ならPhase 4.8で測定する。Decoder identity、chunk Artifact、Frame数、寸法、比較ordinal、平均誤差、orientation、Artifact hashをテスト結果へ保存し、Capture Run ManifestまたはProduction Recovery schemaへ追加しない。

Phase 0.11はFrameごとのfile create／open／close、`.tmp`／rename、content hash、Artifact Registry Entry、Publication Plan Entryを禁止する。nominalではRun chunk 1件だけがこれらの対象となり、Phase 0.11ではchunk確定時を含め`Flush(true)`を要求・実行しない。この削減のために未確定chunkの細粒度Recovery状態機械を追加しない。120 Frame上限を外す、分単位の録画へ進む、またはbounded chunkを正式形式へ昇格する前に、Phase 4.8で正式chunk長、GOP／Container／segment、durability頻度、index／seek、保持期間、payload所有権、hash回数、停止時Publication時間を実測して正本化する。

Phase 0.11ではencoded bytesの決定性、canonical start code／NAL列、独自parserによるH.264完全適合証明、Annex A Level制約のアプリ内再計算、Recovery時のSPS／PPS／Slice意味解析、未確定chunkの部分修復、OS crash／電源断への完全durability、画質、bitrate、長時間安定性のSLAを保証しない。各入力FrameをIDRとしSPS／PPSを反復する要求設定は維持するが、P／B Frame、Frame間参照、PTS／DTS、正式GOP／Container／segment、index／seek、OpenXR Projection Swapchain直接Capture、AMF／QSVその他GPU vendor、Codec比較、zero-copy方式比較、Queue容量調整、詳細性能最適化、長時間または分単位の測定・テストは設計・実装・完了条件に含めず、Phase 4.8以降で実測して判断する。

### 21.16 Phase 0.12～0.14 可変長Trace移行

#### 21.16.1 適用範囲と切替

対象はDomain Trace、`TraceLogger`、Writer、History、Trace bundle保存／読込み、および既存Capture終了処理からproducer停止、最終Drain、seal、保存を呼ぶ接続点だけとする。映像録画、画像Capture、Encoder、Capture Artifact Backendは変更しない。

Phase 0.12～0.14は同一変更系列の内部acceptance checkpointであり、各checkpointの旧新backendをReleaseへ並行搭載すること、二重Event生成、長期互換Adapterを要求しない。Release既定は0.14成立後に一度だけ新経路へ切り替え、同じ変更系列で旧Job Writer、`SealableTraceWriter`、Active Writer Gate、旧`NativeQueue`経路とそれらだけを検査するテストを削除する。現行`trace.bin`／Manifest／bundle v1はPhase 0の完成形式兼移行前形式として固定し、既存成果物を読むv1 Loaderだけを維持する。新形式からv1への完全再exportと新規v1書込みは要求しない。

#### 21.16.2 Writer、Lane、Drain、seal

Run開始前にimmutableなTrace Profileから固定Event mask、producerごとのPayload Ring容量、Runtime Index Ring容量、通常Drainの最大record件数、`MaxPayloadLength`を確定し、0.13以降は同じProfileからHistory Page size／Page数も確定する。各checkpointで使用するpayload、index、Page、cursor、counter、alignment、Page metadataを含む総メモリをchecked算出して一括確保する。具体的な容量値、32 GiB、1 GiB等を既定値、最低値または製品保証にせず、確保不能または不変条件不成立ならRunを開始しない。Run中に領域を拡張／縮小せず、別lane／Pool探索、借用、動的Profile、sampling、severity taxonomyを追加しない。

1 laneは同時に1 producerと1 consumerだけが使う固定容量SPSCとし、backend非公開のBurst互換value-type Writerを構築時に単一laneへbindする。Writerは`IsEnabled(EventMaskBit)`と`TryWrite(RecordKind, PayloadPointer, PayloadLength)`だけを提供し、callerはpayload構築前にmaskを確認して正確な長さを決める。Writerはunmanagedなcaller memoryからlane所有Payload Ringへ必ずコピーし、return後にcaller memoryを参照しない。zero-copy、外部buffer lifetime移譲、managed配列、boxing、文字列化、serialize中の長さ決定を通常writeへ導入しない。

Runtime Index Entryは`RecordKind`、64 bit単調増加`PayloadStart`、`PayloadLength`だけを持ち、永続schemaではない。`RecordVersion`を持たず、payload schemaを変更するときはappend-onlyな別`RecordKind`を使う。WriterはIndex 1件と末尾paddingを含むPayload領域の空きを確認し、payload全体をコピーしてprivate index slotを書いた後、index write positionをrelease公開する。この公開だけをlane上のrecord commit pointとし、consumerはacquire済みentryが指すpayloadだけを読む。容量不足、oversize、内部Rejectは待機、拡張、別lane探索を行わず失敗を返し、lane-local Drop Countをsaturating加算する。Event単位の詳細な失敗Reasonは保存しない。Profileで無効なEventはDropへ数えない。

通常Drainはlaneを固定順round-robinで巡回し、構成された最大record件数で終了する。時間budget、動的quota、厳密なlane間公平性、producer間global sequenceを導入しない。単一lane FIFOだけを保証し、lane間の因果関係はTimestamp、FrameId、FixedStepIdおよびpayload内Domain IDで解釈する。正規停止順は`新規producer受付停止 -> Job完了／Worker停止 -> 全ownerがWriter使用終了 -> 全lane最終Drain -> Drop集約 -> seal`とする。Release Loggerはこのstop／joinを信頼し、Registry、Lease、Receipt、per-event Active Writerで再証明しない。stop／join後のstale Writer使用はReleaseで未定義動作とする。

#### 21.16.3 MemoryBounded Paged History

HistoryはProfileで指定した固定数のPayload PageをRun開始前に確保し、単一History Writerがframed recordを保存順に追記する。各Pageは`CommittedByteCount`だけを持ち、History全体で64 bit `CommittedRecordCount`を1個だけ持つ。History Index、Page状態enum、参照count、Page Lease、実行中export、live Snapshotを設けない。

Page内recordは`RecordLength | RecordKind | Payload`の連続byte列とする。`RecordLength`はLength field自身を除いた`RecordKind + Payload`の合計byte数を表し、Readerは`現在位置 + LengthFieldSize + RecordLength`を次record位置とする。`FrameHeaderSize + MaxPayloadLength <= History Page writable容量`かつ`MaxPayloadLength <= Producer Lane Payload容量`をRun開始前に検証する。recordが現在Page末尾へ全体で収まらない場合は末尾を未使用のまま次Page先頭へ移り、1 recordをPage間で分割しない。

History Writerはrecord全体の空きを確認し、Headerとpayloadをコピーした後だけ当該Pageの`CommittedByteCount`とHistoryの`CommittedRecordCount`を進める。単一Writerかつ停止後だけ読むためatomic更新は要求しない。Page不足、History容量不足、checked演算失敗はgameplayを待機させずHistory Dropとして集約しTraceをIncompleteにする。停止後Viewは各Pageのcommitted prefixだけを列挙し、全recordを別配列へコピーしない。

#### 21.16.4 Trace format／Manifest／bundle v2

新形式は既存Run bundleと同じ`bundle.index`、`manifest.json`、`trace.bin`の3 file構成を維持するが、bundle index、Manifest schema、Trace formatをそれぞれv2として識別し、bundle v1へ偽装しない。Manifest v2は既存Run Contextに加え、Trace format version、固定Event mask、非負64 bit `RecordCount`、`TotalFramedByteLength`、`DroppedRecordCount`を持つ。v1固有の`EventCount`、`TriggerHistoryCount`、`CapturedPostRollCount`、`WasHistoryOverwrittenAtTrigger`をv2へ継承しない。`DroppedRecordCount > 0`を記録時のIncompleteとし、保存失敗ではbundleを公開しない。exact property順、整数表現、File Header、Length／Kind field幅、endianness、magic、format version番号、既知`RecordKind` payload schemaは0.14の実装前Fixtureで固定し、測定前の性能保証にはしない。

Exporterはseal済みPage metadataから`RecordCount`と`TotalFramedByteLength = 全PageのCommittedByteCount合計`をchecked集計し、最終File Headerを最初に書く。続いて各Pageのcommitted prefixだけを順番に一時bundle directory内の`trace.bin`へ直接書き、同じpassでTrace hashと実byte長を確定する。Headerの事後seek、全record配列、v1 record展開、hash目的のfile再読込み、Eventごとのopen／close／flush／renameを行わない。最終Manifest v2と、Manifest／Traceの長さとhashを持つbundle index v2を書いた後、既存と同じ一時bundle directoryから完成directoryへの非上書きrename 1回をPublication pointとする。内側の`trace.bin.tmp`やfile renameを追加しない。

v2では`Flush(true)`、directory metadata flush、journal、OS crash／電源断後のdurability、未完成bundleの部分救済、Crash Recoveryを要求しない。一時directory内の書込み失敗またはrename前の停止では正式bundleとして扱わず、保持済みsealed Historyから再試行する場合も未完成fileを再利用しない。電源断後に完成directoryが残っていても構造、長さまたはhash検証に失敗するbundleは全体をRejectする。

bundle Loaderはbundle／Manifest versionを先に読み、v1は既存Loader、v2はbounded逐次Readerへ送る。v2 Readerはcallerから最大Record Count、最大Trace Byte Length、最大Payload Lengthを受け、配列確保前にHeaderとfile長を検証する。各recordについてLengthの最小値／上限、checked次位置、payload範囲、宣言record数、宣言総byte数、file末尾、bundle indexの長さ／hashを検証する。未知`RecordKind`は`RecordLength`でskipして既知recordの列挙を続け、Reader結果の単純な`HasUnknownRecords`をtrueにするが、それだけで記録時のIncompleteとはしない。構造不正、切詰め、overflow、範囲外length、余分な末尾byte、hash不一致はfile全体をRejectする。未知Kind専用enum、全record cache、random access、永続二次Index、lazy Page cacheを初版へ追加しない。

#### 21.16.5 完了条件と非目標

Phase 0.12では共有locked RMWなし、Run中allocationなし、payload-before-index commit、lane FIFO、wrap／padding、Drop、bounded Drain、stop／join後の最終Drainを検証する。Phase 0.13ではPage境界、commit prefix、総容量、Drop／Incomplete、2 GiB超を単一managed配列なしで表現するchecked容量計算、全件copyなしの停止後Viewを検証する。Phase 0.14では異なるpayload長、未知Kind skip、64 bit件数／長さ、v1／v2分岐、streaming hash、単一directory Publication、破損Rejectを検証する。代表負荷と最大想定producer数でCPU時間、events/s、copy byte、allocation、Main Thread Drain、Drop、History memory、export時間／throughput／peak memoryを測るが、固定倍率、32 GiB実消費、巨大な条件直積、P50／P95／P99製品保証を追加しない。新Test IDは作らず各Phase行の完了条件を使用する。

初版はMemoryBoundedだけとし、Rolling、Background／Segment Writer、保持中Page再利用、可変長recordの分割／連結、Context Registry、sampling、動的Profile、全producer間total order、Timeline random access、部分file修復、敵対的改ざん検知を非目標とする。実測または具体的なログ種別が必要性を示した場合だけ、別Phaseで追加する。

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
