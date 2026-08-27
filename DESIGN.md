# VR斬鉄剣ゲーム 技術設計書

*即時シェーダ切断と非同期メッシュ／物理更新による、低遅延・反復切断パイプライン*

| 項目 | 内容 |
| --- | --- |
| 文書目的 | Codexで継続更新するプロジェクト設計上の正本 |
| ステータス | Draft v1.5 / PoC実装準備・固定Capture Profile／同期映像／未来評価設計段階 |
| 作成日 | 2026-08-21 |
| 最終更新 | 2026-08-27 |
| 想定エンジン | Unity 6.3 LTS 6000.3.22f1 + OpenXR + URP |
| 採用アセット | Synty POLYGON City Pack |
| 初期対象 | PCVR、90Hz基準。Quest単体版は当面スコープ外 |
| 検証用HMD | Meta Quest 3SをQuest LinkでPCVR接続 |

> **設計の核** 刀の放つ斬撃波が触れた瞬間はGPUによる仮切断を表示し、表示メッシュと物理Convexをバックグラウンドで切断して追いつかせる。プレイヤーが感じる応答時間と、正確な幾何・物理更新を分離する。

## 1. エグゼクティブサマリー

本企画は、VR空間内の多様なプロップや人形を、刀の放つ斬撃波に沿って任意方向に両断できるアクションゲームである。最大の体験価値は、斬撃直後に隙間が開いて対象が分離したように見える即応性と、その後に破片が自然に物理挙動へ移行する一貫性にある。

推奨アーキテクチャはUnityを基盤とし、OpenXR、ステレオ描画、シーン管理、Rigidbodyなどを利用しながら、切断判定、仮切断レンダラ、メッシュ切断、Convex切断、世代管理を独自サブシステムとして実装する構成である。フルスクラッチのエンジン開発は行わない。

見た目はSynty POLYGON City Packを素材基盤とし、限定パレット、セル陰影、輪郭線、独自の看板・グラフィティでポップなローポリ都市へ統一する。特定作品の直接模倣ではなく、Y2K的な都市感、誇張されたシルエット、色面の強さをデザイン原則として抽出する。

## 2. 体験目標と設計原則

- 斬撃入力に対する見た目の反応を、幾何切断完了より先に提示する。

- 表示と物理の不一致時間を短くし、プレイヤー身体や周辺破片が透明な旧Colliderへ接触する状態を最小化する。刀は物理衝突させず、切断可能時の論理Sweepだけを使用する。

- 生涯切断数や全Pending Cut数ではなく、実際にBatchへ投入する`TemporaryRenderCapRecordSet`の件数と対象Cut Shellが一時描画コストを決める構造にする。意味上の`ActiveTemporaryBoundarySet`とは分離し、`HasDetached`またはCull失効済み操作で実装簡略化のため残す補助Dormant Capも描画費用と枚数上限へ数える。Suppressed Cap、Fully Fixed Cullされた操作、Committed済み境界は費用へ含めない。

- 表示Mesh、プリプロセス済みSolid Cut Mesh、実行時Cut Shell、物理用Physics Proxyを分離し、入力モデル品質に性能と堅牢性を依存させすぎない。

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
| Future Evaluation Scheduler | 未来イベントDAGを締切、計算費用、信頼度で優先評価し、未Schedule処理の取消、実行済みJob成果物の世代破棄、再利用を管理 |
| Prediction Physics | 必要な局所物理島を独立PhysicsSceneで先読みし、命中予定姿勢を生成 |
| Observability／Trace | Profiler計測、状態イベント、Work Item／Job相関、固定長履歴、異常時保存、Editorタイムラインを提供 |
| Visual Capture | Unity側の選択的片眼録画と異常時静止画をTraceへ関連付け、後期にはOpenXR API LayerによるProjection Swapchain Captureを提供 |
| Asset Preprocessor | Blenderをヘッドレス実行し、ライセンスAssetから基底Solid Cut Mesh／Physics Proxy／検証レポートをローカル生成 |

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

## 5. 即時表示レンダラ

### 5.1 分離表示

元メッシュを論理破片ごとに描画し、各切断面の正負符号に応じてフラグメントをclipする。論理上の切断幅（Kerf）は0とし、自由破片が相対移動した結果としてのみ隙間と断面が見える。単一切断では正側・負側の2インスタンスを描き、自由側へ必要最小限の仮分離Offsetを与える。複数切断では、論理破片が保持する半空間の組み合わせだけを描画する。

破片の表示オフセットはスキニング後またはワールド変換後に加える。スキニング前に加えると、ボーン姿勢によって分離方向が歪むため避ける。

FixedSupportGraph上で連結な切断境界の両側Fragmentがともに固定なら、その`CutBoundaryRecord`の`ExposureState`を`Dormant`とする。本設計ではKerfが常に0であり、Fixed Fragmentの表示Offsetと相対運動も0という不変条件を別途持つため、Dormant判定でKerf、Offset、相対Transform、後続Detached状態を重複確認しない。ExposureStateの判定単位は境界ごとだが、PoCの即時Renderer全体省略は後述する一回の`LogicalCutOperation`単位で行う。同じ親LogicalFragmentへの一回の切断で生じた全直接子がFixedでCull未失効なら、その切断操作の全即時clip、Stencil、仮Cap、Shadow近似を省略する。Unknownがなく一つでもDetached、またはFully Fixed Cull失効済みなら、Fixed同士のDormant境界を含む全非Suppressed Capを通常Batchへ残し、Cap単位の除去やペア追跡を行わない。UnknownがあるIncomplete操作では既知Active Capだけを描く。バックグラウンドの実Mesh切断が完成したら、Fixed Fragmentを同一Transformのまま実断面付きMeshへ差し替えてよい。境界に生じる細い亀裂、輪郭線、線状Z-fighting、軽微なチラツキは「極めて薄い切断痕」として許容する。後続切断でAnchorへ到達できない論理破片が生じた瞬間、その破片に接する過去のDormant境界をまとめてActiveへ変更し、完成済みFragmentはRenderer交換なしで動かし、未完成境界だけを即時レンダラで補う。

### 5.2 仮断面とステンシル

ステンシルは切断そのものではなく、仮断面キャップのマスク生成に使う。プリプロセス済みSolid Cut Meshまたは直前のStable Cut Shellから、Geometry未CommitのPending Cutを適用した論理上の実行時Cut Shellを導出する。意味上の`ActiveTemporaryBoundarySet`は`ExposureState == Active && GeometryState != Committed`、すなわちGeometryが`Pending`または`Ready`の境界集合とする。実際にBatchへ投入する`TemporaryRenderCapRecordSet`は後述する`OperationSupportState`と`FullyFixedCullInvalidated`から別途導出し、`HasDetached`またはCull失効済み操作ではDormant補助Capを含み得る。各Recordに対応する閉じたCut Shellの表裏面から対象境界の内部領域をStencilへ記録し、対象のローカルOBBと切断平面の交差から作る有限な`Cap Bounds Polygon`をStencil非ゼロ領域だけ描画する。

- clip()：物体を正負に分け、隙間の空いた分離表示を作る。

- Stencil：切断平面上で元物体内部に相当する範囲をマスクし、仮断面を塗る。

- 実断面Mesh：バックグラウンド処理完了後に仮断面を置換する。

- Cap Bounds PolygonはOBBの12辺と切断平面を交差させ、epsilonで重複を除いた3～6頂点を平面上で並べて生成する。複数のTemporary Render Boundaryでは、ほかの表示中切断面が定める論理破片の半空間で凸多角形clipし、切断面同士の交差を即時表示へ反映する。SuppressedなPending Cutはこの即時描画用clip集合へ含めない。全直接子Fixedの`LogicalCutOperation`は操作単位で除外し、それ以外の操作ではDormant境界を含む全非Suppressed Capを通常経路へ残せる。

- Cap Bounds Polygonは物体表面との正確な交差輪郭ではないため、最終的な凹形状、穴、部品輪郭はStencilで制限する。実表面との輪郭を三角形化できた場合は実断面Meshとして扱い、Stencilへ重複して依存しない。

- 全直接子Fixedの切断操作は仮断面描画を要求しないが、実断面Meshの生成と公開は停止しない。実断面は共通の片面トゥーンMaterialを基本とし、正負Fragmentで逆向きの法線を持たせる。Cull Offの両面描画は通常カラーPassで常用しない。一つでもDetachedな直接子がある操作でFixed同士のCapを通常Batchへ残した結果の線状亀裂、輪郭線、局所的Z-fightingは許容するが、画面規模の面状Z-fightingや可視Cap欠落は不具合とする。

> **入力品質上の注意** Stencilの表裏カウントは閉じた整合的な形状を前提とする。表示Meshを直接使わず、基底Solid Cut Meshから派生し、現在世代を表すCut Shellをマスク生成へ使う。Edgeが2面に接続するTopological Watertightだけでは十分でなく、面向き、退化、非隣接Faceの自己交差も検証する。面反転のない小さな自己交差はWinding Countの即時表示で見かけ上成立する場合があるが、正式なSolid Cut Mesh入力としては採用しない。

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

- 切断平面は固定上限`MAX_PENDING_CUTS`のInstance Recordに`CutCount`、`CutPlanes[]`、`FragmentSide`、`SeparationOffset`として保持する。切断数や平面値でMaterial、Shader Keyword、Passを増やさず、同じCull群内ではper-instance `clip()`としてBatchを維持する。

- Stable Instanceをclip対応Shadow Shaderへ統合するか、`CutCount = 0`専用の高速経路へ分けるかは実測で決める。全ShadowCasterを常時`Cull Off`にしてDraw群を統合する案は、裏面Raster／overdraw増加を測定せず採用しない。

### 5.5 コスト制御

- 同一物体の`TemporaryRenderCapRecordSet`件数に上限を設ける。初期候補は実際に描くCap Record 2〜4枚とし、`HasDetached`またはCull失効済み操作で残す補助Dormant Capも1枚ずつ数える。Suppressed Cap、Fully Fixed Cullされた操作、Committed済みCutBoundaryRecordは数えない。意味上のActive境界数だけで上限判定してはならない。

- 上限到達時は補助Dormant Capを含む`TemporaryRenderCapRecordSet`を基準に、複数切断をまとめて再構築し、古いGeometry未Commit境界をStable Meshへ焼き込む。`Ready`から実Mesh適用と`Committed`への遷移が同じ描画フレーム境界で成功した後にだけ対応Cap Recordを実描画集合から外し、Active境界集合と切断履歴そのものは独立して保持する。

- 画面外・遠距離・停止中の物体を優先的に確定する。

- 小さすぎる論理破片は描画／物理の対象から外し、簡易デブリへ統合する。

- Stencilは切断面ごとの一時作業領域として再利用し、恒久的なビット割当は行わない。

### 5.6 スクリーンスペースStencil Batch

Stencil Bufferは画面座標ごとに共有されるため、すべての即時切断物体を無条件に同じStencilへ蓄積しない。ただし、現在の全World Cut Plane、各PlaneのFragment Side／半空間、分離Offset、Cap Material、法線、デバッグ色、Fade等が一致する対象は`CapCompatibilityKey`で同じ互換Groupへまとめる。このGroup内ではキャップ描画結果が同一なので、スクリーンスペースで重なっていてもStencilを和集合として共有できる。

StencilはParityの`Invert`ではなく、整合したCut ShellのFront／Back Faceに対するIncrement／DecrementからなるWinding Count方式を使い、Capは`Stencil != 0`で描画する。閉じた部分ではFront／Backが`+1 - 1 = 0`へ相殺され、切断による開口部だけに非ゼロの`Residual Stencil Support`が残る。途中のStencil書き込みが別物体と重なっても、最終的にゼロへ戻る領域は競合としない。互換Group内で複数物体の開口部が重なった場合もCountを1、2、3と保持し、偶数重なりを誤って空洞化しない。8bit CountのWrap／飽和条件、面向き、Depth／clipの非対称はT-067で検証する。

各フレーム、互換Groupをノードとし、左眼または右眼のどちらかで保守的な可視Cap Boundsが重なる、かつ`CapCompatibilityKey`が異なるGroup間へ辺を張る`Stencil Conflict Graph`を構築する。物体OBB投影矩形と可視Cap Boundsはどちらも安全側の非交差証明に使い、各眼でいずれかが非交差ならその眼では競合しない。次数または画面面積の大きい順にFirst-Fit Greedy Coloringし、同じColor内では「全眼で可視Cap Boundsが非重複」または「重複してもキャップ互換」のどちらかを保証する。

各Colorについて、対象Rectの予約Stencil領域をクリアし、Color内の全Cut Shellを共通Stencil Volume Phaseへ投入した後、対応する全Cap Bounds Polygonを共通Cap Phaseへ投入する。Color内では非互換な`Residual Stencil Support`同士が重ならないため、Rawな途中書き込みの重なりを許容しつつ、物体別Stencil IDを持たず同じStencil操作を再利用できる。Shader Passは全対象で共通化できるが、Mesh／Material等により各Phaseが複数Drawへ分かれることは許容する。

- Broadphaseでは分離Offsetと安全Marginを含む物体OBBの左右眼投影矩形を使う。重なる組だけ、表向きのOBB切断面から得たCap Bounds Polygonを左右眼へ投影して再判定する。どちらの判定も非交差なら安全という悲観的な証明として扱い、Near Plane交差、Raster／MSAA、頭部移動誤差を考慮してBoundsを保守的に拡張する。

- `CapCompatibilityKey`は順序を正規化した表示対象`CutPlaneId`列、Side Mask、Offset、Material／Debug Stateから作り、Raw floatだけをHashの正本にしない。同じSlash由来でも、対象が別々に移動・回転した後は現在のWorld Planeをepsilon比較し、一致しなければ別Groupへ分離する。片方だけに追加Temporary Render Boundaryがある場合も互換ではない。

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

- Cap処理順は`Support Connectivity更新 -> 置換直接子／Active化境界の祖先Operation Cull失効 -> 過去境界Dormant／Active再評価 -> OperationSupportState三値集約 -> FullyFixedCullEligible導出 -> ActiveTemporaryBoundarySet／TemporaryRenderCapRecordSet構築 -> 両眼Frustum／Facing Cull -> CapCompatibility Group -> 全Cap不可視Group Cull -> Stencil Conflict Graph -> Greedy Coloring -> Stencil Volume／Cap描画`とする。Cull失効と境界Active化の順序を逆転させない。切断操作単位の固定長Child Support集約だけを早期判定に使い、Cap pair／Coverage判定や、描画対象操作内のCap Buffer compaction／Mesh部分更新はPoCで行わない。

- PoCは単純な全組み合わせ`O(M^2)`とGreedy Coloringを使用する。Pending対象数が増えてCPU費用が問題になった場合だけ、スクリーングリッド／Sweep and Pruneへ置換する。最小彩色は求めない。

- 可視Cap Boundsを`Residual Stencil Support`の保守的上界として使用する前提は、整合したCut Shell、Front／Backで対称なclip、相殺を妨げないDepth／Stencil設定、十分なRaster／MSAA Marginである。非閉形状、Near Plane、片面だけのDepth失敗などでCap外に非ゼロ値が残り得る場合は同一Batchへ入れず、安全なFallbackへ送る。

- Colorごとに深度全体を消去せず、予約Stencil Bitだけを対象Rect／ScissorでZeroへ戻す。Stencil Bitの恒久的な物体割当は行わず、URPが使用するBitとの競合を避ける。

- 全対象が重なる最悪時にColor数がPending対象数まで増えることを許容しつつ、最大Color数とStencil GPU予算を設ける。超過時は遠距離／小画面対象のキャップ省略、単色VFX化、表示Mesh Job優先度引上げの順で品質低下し、誤ったStencilを描かない。

## 6. 表示メッシュ切断

### 6.1 入力と出力

| 区分 | 内容 |
| --- | --- |
| 入力 | 頂点、法線、接線、UV、色、submesh、切断平面、論理破片ID、世代番号 |
| 処理 | 三角形分類、交差頂点生成、属性補間、輪郭ループ構築、断面三角形化 |
| 出力 | 正負破片Mesh、断面submesh、Bounds、体積候補、コミット用メタデータ |

### 6.2 Unity実装方針

- C# Job SystemとBurstでアンマネージデータを処理する。

- 読み取りには`Mesh.AcquireReadOnlyMeshData`、生成には`Mesh.AllocateWritableMeshData`を用いる。ReadOnly `MeshDataArray`は保持中に元Meshを変更しなければ原則コピーなしのSnapshotとしてJobから参照し、複数Meshは一括取得してSafety Tracking費用を抑える。連続切断ではRendererのMeshを毎回再取得せず、Stable Cut ShellのNative表現を次世代の正本として引き継ぐ。

- Jobは頂点、Index、Vertex Layout、SubMesh、Bounds候補をWritable `MeshData`へ出力する。頂点／Index数が事前に定まらない処理は`Count Job -> Native領域確保 -> Write Job`の二段階を基本とする。

- 完成データはGeneration検証後、メインスレッドで`Mesh.ApplyAndDisposeWritableMeshData`により`UnityEngine.Mesh`へ適用し、Renderer参照だけを描画境界でCommitする。重い頂点処理をメインスレッドへ戻さない。

- 切断に交差した論理破片だけを再構築し、物体全体の再処理を避ける。

- 頂点や辺の近傍を通る切断にはepsilon規則を統一し、退化三角形を生成しない。

- 高速な断面Loop構築は、切断平面との交差線分が交差せず、各輪郭頂点の次数が2となるGeometrically Valid Solidを前提とする。自己交差入力に対する一般的な2D Arrangement／Winding領域分解は初期スコープ外とし、前処理で除去できない入力はVoxel結果へFallbackまたは`NeedsReview`とする。

## 7. 物理切断

### 7.1 一時状態

ColliderのBake／cookingは視覚切断のクリティカルパスに含めない。`Active`な切断境界は命中フレームからclipと仮断面を表示し、物理状態が許可する場合は相対移動による隙間も表示する。ConvexとBakeが間に合わなくても視覚応答を待たせない。`Dormant`境界は単独では即時表示を要求しないが、Operation規則による補助Cap描画を妨げない。補助Capが描かれてもDormant側の運動は起動しない。切断直後の物理状態はFragmentGroup内の全LogicalFragmentの支持分類を集約し、`PendingPhysicsSplit`、`PendingAnchoredSplit`、`PendingSupportClassification`のいずれかへ一意に決める。いずれも旧Convexを支持用として持つ1つの`FragmentGroup`を維持し、旧Convexを複製して双方へ付与しない。複製すると、不自然な押し出しや存在しない中央部への接触が発生する。

- 刀は旧Colliderを含む物理Colliderへ接触させず、Edge Direction Gate成立中の論理SweepだけでHitを判定する。プレイヤーの手・身体が旧Colliderへ触れる場合の例外方針は別途T-005で評価する。

- `PendingPhysicsSplit`中は原則1つのRigidbodyと旧Colliderを物理状態の正本とし、左右の表示破片は独立した接触、落下、回転を行わない。外部から受けた力と接触ImpulseはFragmentGroup全体へ作用する。

- FragmentGroup内に支持分類未完了、世代不一致、または接続が曖昧なLogicalFragmentが1つでもあれば、Group物理状態は`PendingSupportClassification`へ入る。この状態では旧Rigidbody、Collider、ConstraintおよびTransformを変更せず、Group全体の分離Offset、切断Impulse、自由側解析運動を禁止する。一方、境界単位の描画判定は独立して維持し、`Active`と確定済みの境界ではclip、Stencil、仮Cap、非運動の切断演出を許可し、`Suppressed`境界ではすべての即時切断表示を禁止する。支持再分類と背景Geometry処理を進め、全LogicalFragmentが既知になった時点で、全て自由なら`PendingPhysicsSplit`、1つ以上の固定側を含めば`PendingAnchoredSplit`へ遷移する。再分類不能が予算時間を超えて継続する場合は、保守的な未分裂Fallbackを維持してTraceへ記録し、同期的な重いGraph処理やcookでフレームを停止させない。

- 地面、壁、建物基礎などへ固定された対象は例外として`PendingAnchoredSplit`へ入る。分離運動または切断Impulseを適用する前に、`FixedSupportAnchor`を切断平面の正負半空間へ分類し、必要最小限の接続判定を完了する。固定側を含む旧Rigidbody／旧Collider全体へ切断Impulseを与えてはならない。

- 単一の連結Convexと1個以上のFixedSupportAnchorだけで表せる対象は、各Anchorについて`dot(planeNormal, anchorPosition) + planeDistance`の符号を評価するだけで固定側を決める。正側だけにAnchorがあれば正側固定、負側だけなら負側固定、両側なら両側固定、どちらにもなければ通常の自由分裂とする。平面から`anchorEpsilon`以内のAnchorはPoCでは保守的に両側固定として扱い、破断可能な固定具は後続仕様とする。

- Compound Convex、建物チャンク、複数支持部を持つ対象は、プリプロセス済み`FixedSupportGraph`を使用する。Physics Proxy／構造チャンクをNode、切断前の接続をEdge、FixedSupportAnchorをRootとして保持し、切断面で失われるEdgeを除いた後にRootから到達可能な成分を固定、到達不能な成分を自由と分類する。これは完全なConvex B-rep切断、質量特性計算、`Physics.BakeMesh`より先に行う軽量判定である。

- `PendingAnchoredSplit`中は固定側の表示Offsetを0とし、自由側だけを衝突なしの解析運動で視覚的に分離できる。元の未切断Colliderは固定状態のまま残すため、周辺物体との一時的な透明接触や隙間内Colliderは許容する。切断幅は0であり、両側固定なら切断をDormantにして即時分離を見せず、どちらにも分離Impulseを与えない。実Fragment Meshが完成した時点で同一Transformのまま公開し、線状の切断痕が見えることは許容する。

- FixedSupportGraphは最新の1切断面だけでなく、現在ObjectGenerationへ蓄積された全切断面で区切られた論理破片ごとにAnchor到達性を再評価する。例えば建物の最初の縦切断で両側が基礎へ接続していればDormantのままとし、交差する2面目によってAnchorなしの部品が初めて生じた時点で、その部品に接する1面目と2面目の断面を同時に可視化して分離する。

- FixedSupport分類は命中フレーム内で完了する固定長処理を目標とし、少数Anchorの半空間分類は同期実行してよい。SlashFrameと候補対象の未来姿勢が命中前に確定している場合は投機評価し、実命中、ObjectGeneration、Anchor／Graph世代、切断面の一致をCommit条件とする。不一致または未完了時は対象境界を`Suppressed`として全即時表示と運動を抑止し、再分類へ送る。

- 断面間の小さな見た目上のめり込み、周辺物体と表示破片の一時的なめり込み、見えている切断隙間に旧Colliderが残ることを許容する。違和感を限定するため、Pending中の仮分離Offsetには物体寸法と想定Impulseに基づく上限を設ける。Kerfは常に0であり、仮分離Offsetとは別パラメータとする。

- 後続の斬撃Hitと幾何切断は旧Colliderではなく、Pending Cutを適用した論理破片とCut Shellを参照する。物理が未分離でも、表示・切断履歴・世代管理は切断済みとして進める。

- Convex生成と`Physics.BakeMesh`をバックグラウンドで完了させ、成果物と世代が有効な場合に初めて左右を複数Rigidbodyへ分割する。Bakeの遅延や失敗は即時表示を巻き戻す理由にしない。

- Collider差し替えとRigidbody分裂は物理ステップ境界で行う。

- Pendingが予算時間を超えた場合はTraceへ`PhysicsSplitTimeout`を記録し、旧Collider共有を継続しながら優先度を引き上げる。無効なConvexは簡易Proxy、Compound Primitive、または非物理デブリへ品質低下させ、メインスレッドで同期cookしてフレームを停止させない。

### 7.2 Convex切断と運動継承

- 凸多面体を切断平面でクリップする。結果の正負側も凸となる。

- Physics ProxyのwatertightなConvex B-repをNative形式で保持し、頂点の正負分類、各面のPolygon clipping、交点／切断面Polygon生成、重複頂点統合、凸性・閉性検証、体積・重心・慣性計算をJob＋Burstで行う。一般凸包の再計算は原則行わない。

- 出力数が不定なため、`ConvexCountJob -> Native領域確保 -> ConvexWriteJob -> ValidationJob`を基本Pipelineとする。多数破片は同種段階をBatch化し、1破片ごとの極小Job乱発を避ける。

- 交差するConvexだけを切り、片側に完全にあるColliderはそのまま該当破片へ移す。

- 体積比で質量を配分し、各破片の重心と慣性テンソルを更新する。

- 各破片の初期線速度を`v_child = v_group + omega_group x (COM_child - COM_group)`、初期角速度を`omega_child = omega_group`として、Pending中にFragmentGroupが受けた運動を引き継ぐ。

- 物理Commit時は表示上の分離位置とCollider位置を一致させ、切断面法線方向へ小さな分離Impulseを加える。Pending中に表示だけが大きく開き、Commit時に遅れて強く跳ねる動きは避ける。

- `PendingAnchoredSplit`のCommitでは、Anchorから到達可能な破片を静的／Kinematicまたは元の固定Constraintへ残し、到達不能な自由破片だけにRigidbody、継承速度、分離Impulseを与える。複数Anchorが切断面の両側へ残る場合は両側を固定し、接続グラフ上で自由と証明できない破片へImpulseを与えない。

- 表示用MeshとCollider用Meshを分離し、Collider cooking用形状は低頂点・閉形状に保つ。

- 検証済み左右ConvexをWritable `MeshData`へ出力し、メインスレッドで別々の`UnityEngine.Mesh`へ一括適用する。そのMesh ID列を`IJobParallelFor`へ渡し、`Physics.BakeMesh(meshId, true, cookingOptions)`をバックグラウンド実行する。同一Meshを複数Jobから同時にBakeしない。

- Bake Job完了後も即時適用せず、`SlashId`、`ObjectGeneration`、入力Physics Proxy世代、Cooking ProfileをCommit Controllerで検証する。有効な成果物だけを物理ステップ境界で左右の`MeshCollider.sharedMesh`へ設定し、Rigidbody分裂と運動継承を行う。Schedule済みJobは中断せず、古い成果物は回収する。

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
| 表示Mesh | 頂点／Triangle平面分類、Count、Write、交点統合、断面Loop／三角形化、接続成分、Metadata、Writable MeshData構築 | 入力／出力Triangle数、交差Edge数、断面Loop数、Fragment数、累積切断面数 |
| Physics Convex | Convex Count、Polygon clipping、切断面生成、Write、Validation、体積／重心／慣性、Collider用MeshData構築 | Convex数、各Convexの頂点／面数、交差Convex率、出力Convex数 |
| Temporary Low-Poly Proxy | Bounds／切断面からの簡易表示Proxy、簡易Convex、Compound Primitiveまたは汎用ローポリFallbackの生成 | 目標Triangle／Primitive数、Fragment数、入力Bounds／切断面数 |
| Cook／Commit | `Physics.BakeMesh`のFast Cook／Fast Simulation、Mesh公開、Collider Commit | Convex頂点数、Bake数、Batch Size、Profile、同時実行数 |

同じPure Native入力と出力Bufferを使い、表示Mesh／Convex／Temporary Proxyの計算Kernelだけを同期実行する`Single-Thread Kernel`と、実際の`Schedule -> Worker実行 -> Complete`を使う`Job Batch`を分離する。前者は`µs/op`、入力／出力要素当たり時間、P50／P95／P99を記録し、Job Schedule、GC、Unity Object生成を含めない。Unity API境界を含む`Physics.BakeMesh`、Mesh公開、Collider CommitはPure Kernel値へ混ぜず、直列の単発LatencyとBatch時のEnd-to-End値として別記する。Job側は`cuts/s`、`input triangles/s`、`output triangles/s`、`convexes/s`、`cooks/s`、Job End-to-End latency、Schedule時間、Worker占有率、Main Thread Commit時間を記録する。単発Jobのレイテンシと十分なBatchを連続投入した定常Throughputを混同しない。

固定Datasetには、公開可能な合成Fixtureをcanonical正本として、表示Mesh 500／1,000／3,000／10,000／30,000 Triangle級、Convex 8／16／32／64／128／255頂点級、1／4／16／64 Convex、2／4／8 Fragment、中央切断／端切断／非交差、単一／複数断面、単純／複数Cap Loopを含める。暫定Proxyは50／100／250／500 Triangleまたは1／4／16 Primitive級を初期候補とする。Phase 0.2で自動選抜したSynty由来の`LicensedRepresentative` Render／Solid／Convex Fixtureも非公開の補助Suiteとして同じ測定を行い、Render／SolidはOriginal、100、500、1,000、2,000、5,000 Triangle級のDirect Variantに加え、Voxel64／128／256基底と限定Post-Decimateを比較する。合成Fixtureの代替や全Asset互換性の証拠にはせず、公開結果から入力GeometryやAsset対応を復元できるデータは保存しない。

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
| `BenchmarkStage` | string enum | 必須。`WholePipeline`／`PlaneClassification`／`Count`／`Write`／`IntersectionMerge`／`CapLoopBuild`／`CapTriangulation`／`Connectivity`／`Metadata`／`PolygonClip`／`CutFaceBuild`／`Validation`／`MassProperties`／`DescriptorBuild`／`MeshDataBuild`／`ProxyGeneration`／`NativeBoundaryTransfer`／`MeshApply`／`HullComputation`／`PhysXFormatBuild`／`StreamSerialize`／`StreamLoad`／`DirectInsertion`／`Bake`／`Schedule`／`WorkerExecution`／`Complete`／`Commit` |
| `ExecutionMode` | string enum | 必須。`SingleThreadKernel`／`SerialApiLatency`／`JobSingle`／`JobBatch`／`MainThreadCommit` |
| `BenchmarkMetric` | string enum | 必須。`Latency`／`Throughput`／`InputRate`／`OutputRate`／`WorkerOccupancy`／`ManagedAllocation`／`NativeMemoryPeak`／`FailureRate`／`ScheduleCount` |
| `MeasurementUnit` | string enum | 必須。`Microseconds`／`MicrosecondsPerOperation`／`OperationsPerSecond`／`CutsPerSecond`／`InputTrianglesPerSecond`／`OutputTrianglesPerSecond`／`ConvexesPerSecond`／`CooksPerSecond`／`Percent`／`Bytes`／`Count`／`FailuresPerMillionOperations`から1つだけ選ぶ |
| `TraceRunManifestContentSha256` | string／null | Trace参照時は小文字64桁`[0-9a-f]{64}`、未参照時は厳密に`null` |

canonical JSONは全propertyを上表順序で常に出力し、UTF-8 BOMなし、余分な空白と末尾改行なし、不変Cultureの数値表現とする。nullable propertyも省略せず上表の条件で文字列またはJSON `null`を出力する。Cook Targetは`UnityBakeMesh`と3つの`NativePhysX*`、Native Targetは3つの`NativePhysX*`と定義する。CodecはTargetとStage、ExecutionMode、`NativePhysXVersion`、`CookingProfile`の組合せを検証し、`WholePipeline`以外のStageを無関係なTargetへ指定できないようにする。

`BenchmarkTarget × BenchmarkStage`の許可集合は次を正本とする。`Schedule`／`WorkerExecution`／`Complete`はJob実装を持つTargetだけで使用し、表にない組合せはCodecでRejectする。`WholePipeline`は対象のEnd-to-End系列であり、下位Stageの代用として工程別必須系列を省略してはならない。

| BenchmarkTarget | 許可するBenchmarkStage |
| --- | --- |
| `DisplayMeshCut` | `WholePipeline`、`PlaneClassification`、`Count`、`Write`、`IntersectionMerge`、`CapLoopBuild`、`CapTriangulation`、`Connectivity`、`Metadata`、`MeshDataBuild`、`Schedule`、`WorkerExecution`、`Complete` |
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
| `PlaneClassification`、`Count`、`Write`、`IntersectionMerge`、`CapLoopBuild`、`CapTriangulation`、`Connectivity`、`Metadata`、`PolygonClip`、`CutFaceBuild`、`Validation`、`MassProperties`、`MeshDataBuild`、`ProxyGeneration` | `SingleThreadKernel`、`JobSingle`、`JobBatch` |
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
| Shared | 3 | 必要なLogicalConvexFragmentの一部または全部を複数RenderFragmentが共有 | Shared連結成分へ解決Roleを付与し、小さく非重要な非代表だけをデブリ候補にする。複数が大型なら共有状態を維持してConvex分割を待つ |
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
| Pending Physics Split | FragmentGroupの1 Rigidbody／旧Colliderを共有し、表示と論理破片だけが分離済み | Convex生成とBakeを待ちながら、後続切断と外力を受理 |
| Pending Support Classification | FragmentGroup内にUnknownなLogicalFragmentが1つ以上あり、物理分裂方法をまだ決定できない | 旧Rigidbody／Collider／Constraint／Transformを維持し、Group全体のOffset、Impulse、解析運動を禁止する。支持再分類と背景Geometry処理を進めつつ、既知のActive境界だけはclip／Stencil／仮Capを許可する |
| Pending Anchored Split | 固定側分類済みだがCollider未分裂。旧Colliderは固定したまま、固定側は無移動、自由側だけを衝突なしで仮表示 | Anchor／接続判定結果を維持し、完全Convex切断とBakeを待つ。共有物理へ切断Impulseを与えない |
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

### 10.2 切断可能アセットの三層構造

| 層 | 用途 | 品質契約 |
| --- | --- | --- |
| Display Mesh | 通常表示と最終破片 | 外観優先。複数submeshを許容 |
| Solid Cut Mesh | 初回の内部判定と実行時Cut Shellの基底 | 閉じたwatertight形状、向き整合、退化面なし |
| Physics Proxy | 接触とConvex切断 | 少数の低頂点Convex／Compound |

Blender側の共通変換工程として、Transform適用、原点・単位統一、共通マテリアル化、三角形化、Micro Attachment候補の連結成分抽出とRecipe分類、Solid Cut Mesh生成、Physics Proxy生成、Unity向け書き出しをプリセット化する。実行時Cut ShellはUnity側でSolid Cut Meshから派生させる。Micro Attachmentには安定した`AttachmentId`、Bounds、Anchor、本体に対する体積比、重要部品除外フラグを出力する。

#### 10.2.1 早期Licensed Fixture選抜

Phase 5.5の全Asset対応前に、Phase 0.2でSyntyの多数モデルを固定版Blenderへ一括投入し、簡易処理だけで成功した少数を表示テストと性能測定へ使用する。これは製品用Asset前処理の前倒しではなく、手作業、Asset別Recipe、最終外観調整を原則行わない使い捨て可能な選抜工程である。失敗Assetを個別修理して網羅率を上げず、時間上限または検証失敗で即Rejectして次の候補へ進む。

```text
Synty Source FBX群
  -> Import／Transform・単位適用
  -> Object／Material／Triangle統計
  -> 三角形化
  -> 重複頂点・退化面・孤立要素の最低限除去
  -> 面向き再計算
  -> 基底Render／Solid／Convex Gate
  -> Original／Direct Decimate系列生成
  -> Voxel64／Voxel128／Voxel256基底と限定Post-Decimate系列生成
  -> VariantごとのRender／Solid再検証
  -> 成功Fixtureだけを非公開Datasetへ固定
```

選抜Tierは次に分離する。同一Assetが複数Tierへ合格してもよい。

| Tier | 用途 | 早期合格条件 |
| --- | --- | --- |
| `Render Fixture` | 即時clip、Mesh切断Kernel、MeshData公開、見た目確認 | Profileのfinite／epsilon／Bounds／Triangle／連結成分Gateを満たす。開放面、複数Submesh、複数連結成分を許容 |
| `Solid Fixture` | Cap Loop、反復切断、Stable Fragment Mesh | Render条件に加え、Boundary／Non-Manifold／向き不整合／自己交差が0。Profileの径／平面誤差／個数内の穴だけ自動封鎖でき、単一Presetの粗いVoxel Solid化も許可 |
| `Convex Fixture` | Convex切断、`Physics.BakeMesh`、Cook Probe | Solidまたは単純連結成分から、ProfileのHull数、Hull頂点／Face数、合計頂点、正体積上限内の単一Convex／簡易Compoundを生成できる |

Render／Solid Fixtureには、三角形化後の`Original`、元表面へ直接適用する絶対Triangle Target `Tri100`／`Tri500`／`Tri1000`／`Tri2000`／`Tri5000`、Topologyを再構成するVoxel Remesh系列を候補として持たせる。絶対数を主軸とし、Reduction比は`ActualOutputTriangleCount / SourceTriangleCount`から導出する。Direct DecimateとVoxel後Post-Decimateにはそれぞれ固定Presetだけを使用し、手動ウェイト、局所修正、Target別の見た目調整を行わない。

- Direct Decimateでは、元MeshがTarget以下なら増やさず、そのTargetは`NoOp`としてReportだけへ記録してGeometryを複製しない。元MeshがTargetを1 Triangleでも上回る場合は削減率にかかわらず生成を試みる。Voxel後Post-DecimateもVoxel基底がTargetを上回れば同じ規則で生成する。

- 異なるTargetが同じ出力hashになった場合はGeometryを1件へ重複排除し、ReportにAlias関係を残す。

- 各Variantは実際の出力Triangle数でRender Gateを再検証する。Solid Variantはさらにwatertight、面向き、退化、自己交差を再検証し、失敗したTarget VariantだけをRejectする。元Assetや別Targetまで連鎖Rejectしない。

- 元から100／500 Triangle級の小プロップは`Original`として低Triangle帯へ含め、より大きなAssetをTri100／Tri500へ強制削減したVariantと区別する。極端なReduction Variantは形状検証を通れば性能限界測定用`BenchmarkOnly`として保持できるが、見た目代表値には使用しない。

- Render／SolidのTriangle TargetとConvexの頂点／Hull／Compound削減は別系列とし、`Tri100`等をCollider品質の指定として解釈しない。

Voxel Remesh基底はSourceとTriangle数が同じ、近い、またはSourceより増える場合でも、閉形状化、自己交差の解消可能性、連結、面配置が異なるため生成する。最長ローカルBounds辺を基準に`Voxel64`=`BoundsMax / 64`、`Voxel128`=`BoundsMax / 128`、`Voxel256`=`BoundsMax / 256`の相対Voxel Sizeを初期Presetとし、World Scaleだけで解像度が変わらないようにする。Voxel基底とSourceの出力hashが一致する場合だけAlias化できる。

Variant爆発を避けるため、初期Post-Decimate行列は次へ限定する。`Base`はVoxel Remesh直後を意味する。

| Voxel基底 | 生成するPost-Decimate候補 |
| --- | --- |
| `Voxel256` | `Base`、`Tri5000` |
| `Voxel128` | `Base`、`Tri2000`、`Tri1000` |
| `Voxel64` | `Base`、`Tri500`、`Tri100` |

Voxel Variantは`fixture_017.render.vox128.base`、`fixture_017.solid.vox128.tri1000`のようにTierを含む`DatasetCaseId`を使う。Voxel基底と各Post-Decimate結果はRender／Solid Gateを独立に通し、形状検証を通ってもSilhouette／表面偏差が大きい結果は`BenchmarkOnly`へ分類する。簡易なBounds差、体積変化率、元表面へのsampled距離はReportへ残すが、Phase 0.2ではSurface Projectionや手動修正を行わない。

早期Fixtureの`DatasetCaseId`は`{SourceFixtureId}.{TierToken}.{VariantId}`で構築し、TierTokenを`render`／`solid`／`convex`へ固定する。例えばDirect Decimateは`fixture_017.render.original`、`fixture_017.solid.tri100`、Voxelは上記命名を使う。SourceFixtureIdは最大64文字、VariantIdは最大48文字とし、構築結果が既存Manifestの`[A-Za-z0-9._-]{1,128}`へ収まり、Dataset内で一意であることをCodecが検証する。同じSourceとVariant名が複数Tierで合格しても別caseになる。Benchmark時の実入力は各生成Variantなので、既存`GeometryBenchmarkRunManifest.InputTriangleCount`には`ActualOutputTriangleCount`を格納し、`OutputTriangleCount`は切断等のBenchmark対象処理後のTriangle数として従来どおり使用する。Source、Tier、Process Mode、Voxel Size、Post-Reduction Target、Reduction比、Applied状態はDatasetCaseIdで対応する`EarlyFixtureSelectionReport`から復元し、Benchmark schemaへ意味の重複するpropertyを追加しない。

早期工程ではTrusted Exteriorへの投影、製品品質の見た目を保つReduction、UV／Material再構成、Micro Attachment／FixedSupportGraph、意味を伴う開口保持、車・建物別Recipeを必須にしない。ProfileのHard Bounds／Volume、自己交差、Boundary／Non-Manifold、決定論的Triangle／Component／Voxel Cell／Solid Candidate Pair上限を満たせないVariantはGeometryRejectedまたはProfileUnsupportedとする。120秒／4 GiBの運用上限超過はResourceLimitExceededとして再試行し、形状不合格にはしない。未採用Asset／VariantはPhase 5.5まで保留する。

簡単なAssetだけが残る選抜バイアスを隠さないため、投入総数、Tier別合格数、`AssetCategory`、固定境界の`SourceTriangleBand`、`GeometryProcessMode`、`ReductionVariant`、`SourceTriangleCount`、`ReductionTargetTriangleCount`、`ActualOutputTriangleCount`、`ReductionRatio`、`ReductionApplied`、`VoxelResolutionCells`、`VoxelSize`、`PostReductionTargetTriangleCount`、Bounds差、体積変化率、sampled表面距離、連結成分、Boundary Edge、非多様体Edge、向き不整合Edge、自己交差、全Attemptの処理時間／Peak Working Set／Tool結果、Reject Stage／Reasonを`EarlyFixtureSelectionReport`へ保存する。家具、車、建物、道路設備、小物と複数Triangle帯から少数ずつ固定し、最速の単純形状だけに偏らせない。ただし、この合格集合からSynty全体の互換率やPhase 5.5の成功率を主張しない。

公開可能な合成Fixtureをcanonical Benchmark Datasetの正本として維持する。Synty由来Fixtureは同じHarnessとManifest／Result schemaで測る非公開の`LicensedRepresentative` Datasetとし、合成入力から得た容量式が実Asset分布でも大きく外れないかを確認する補助系列に限定する。入力Geometry、派生Mesh、選抜レポートのAsset名対応表は非公開Asset Repoへ置き、公開RepoにはライセンスGeometryを含まないScript、Schema、匿名化した集計だけを置く。公開可能性が不明な結果は非公開を既定とする。

##### EarlyFixtureSelectionProfile v1

選抜Gate、形状品質区分、決定論的入力上限、運用上の資源上限はversion付きcanonical JSON `EarlyFixtureSelectionProfile`へ固定する。Profile v1のproperty順と値は次を初期正本とし、変更時はProfile hashと全派生Fixtureを無効化する。

| Property | JSON型 | v1値／意味 |
| --- | --- | --- |
| `SchemaVersion` | integer | `1` |
| `ProfileId` | string | `early-synty-v1` |
| `AssetCategories` | string array | 固定順で`["Furniture","Vehicle","Building","RoadEquipment","SmallProp","Character","Other"]`。Reportで許可するカテゴリ集合 |
| `SourceTriangleBandUpperBounds` | integer array | 固定長5、厳密に`[100, 500, 1000, 2000, 5000]`。Source Triangle帯の上限 |
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
| `RepresentativeVolumeError` | number | `0.10`。SolidのSource比体積誤差 |
| `HardVolumeError` | number | `0.50`。Solidで超過時はGeometry Reject |
| `SurfaceSampleCountPerDirection` | integer | `4096`。Source hash由来seedの面積加重sample |
| `MaxSelfIntersectionCount` | integer | `0`。隣接面を除くTriangle交差数 |
| `SolidIntersectionAlgorithm` | string enum | `ClosedTriangleDistanceV1`に固定。別実装・別predicateへの暗黙Fallbackは禁止 |
| `MaxConvexVerticesPerHull` | integer | `255` |
| `MaxConvexFacesPerHull` | integer | `255` |
| `MaxCompoundHullCount` | integer | `16` |
| `MaxCompoundTotalVertices` | integer | `2048` |
| `MaxSourceTriangleCount` | integer | `200000` |
| `MaxVariantTriangleCount` | integer | `200000` |
| `MaxConnectedComponentCount` | integer | `256` |
| `MaxEstimatedVoxelCellCount` | integer | `16777216` |
| `MaxSolidCandidatePairCount` | integer | `2000000`。epsilon拡張AABB broad phase後の一意Triangle pair上限 |
| `SoftTimeoutSeconds` | integer | `120`。資源状態判定専用で形状Gateに使わない |
| `RetryTimeoutSeconds` | integer | `300` |
| `MaxWorkingSetBytes` | integer | `4294967296` |
| `ResourceRetryCount` | integer | `1`。再試行は単一Blender Process、並列なし |

Render Gateは全頂点／属性が有限、Triangleが非退化、BoundsDiagonalがProfile範囲内、非ゼロextent軸が2以上、出力Triangle／連結成分が上限内であることを要求する。Triangle非退化の最終定義は、候補をZCG座標binary32へ量子化した後の`ZcgNumericKernelV1`による`twiceArea > epsArea`とする。Solid GateはさらにBoundary Edge=0、Non-Manifold Edge=0、向き不整合=0、自己交差=0、正の有限体積を要求し、ZCG decode後に同じGateを必ず再実行する。小穴自動封鎖はProfileの径、平面誤差、個数をすべて満たすBoundary Loopだけに適用する。

Convex GateはHull数1..16、各Hullの頂点4..255、Face 4..255、全Hull頂点合計2048以下、各Hullの正の有限体積を要求する。上限を超える形状を暗黙に再簡略化せず、そのConvex VariantをGeometry Rejectする。

SourceとVariantのBounds extent誤差25%超、中心移動5%超、Solid体積誤差50%超はGeometry Rejectとする。Bounds extent相対誤差はSource extentがAsset epsilonを超える軸だけで求め、薄い／平面軸は絶対誤差がAsset epsilon以下かを検査する。Hard Gate内でも、Bounds extent誤差5%、双方向sampled表面距離P95 2%、Solid体積誤差10%のいずれかを超えたVariantは`BenchmarkOnly`とし、見た目代表値へ使わない。これにより「Bounds妥当」「主要Silhouetteが崩れる」を数値判定へ置き換える。

形状偏差のSource基準はRender Variantでは正規化済みOriginal Render、Direct Solid Variantでは封鎖後の検証済みOriginal Solidとする。Voxel Solidで比較可能なSource Solidがない場合、VolumeErrorは`null`としてBounds／sampled表面距離／Solid Gateだけを適用する。`null`を0誤差として扱わない。

Source／Variant Triangle、連結成分、推定Voxel Cell、Solid Candidate Pairの決定論的上限超過は`ProfileUnsupported`とする。同じ入力では再試行してもCandidate Pair数が変わらないため、`MaxSolidCandidatePairCount`超過をResource retryへ流さない。一方、上限内の処理におけるwall-clock、Working Set、Tool crash等は形状不合格にせず`ResourceLimitExceeded`または`ToolFailed`とする。最初の資源超過後は同じ入力hash、Profile、Script、Presetを単一Process・並列なしで1回だけ再試行し、300秒または4 GiBを再度超えた場合は`ResourceDeferred`とする。最初の試行だけが上限へ達し、再試行が処理完了した場合の最終Statusは結果に応じて`Selected`、`BenchmarkOnly`、`GeometryRejected`、`ProfileUnsupported`、`NoOp`または`Alias`とする。各試行はEntry内の固定順`Attempts`へ独立保存し、初回がTimeout／MemoryLimit／ToolFailureのどれだったか、時間、Peak Working Set、Tool終了結果を失わない。`ResourceLimitExceeded`は再試行待ちの中間Statusであり、このStatusを含むReportからDataset Index／Receiptを確定してはならない。再試行完了後は決定表の完了Status、`ResourceDeferred`または`ToolFailed`へ必ず収束させる。Resource状態のVariantはLicensed Datasetへ入れず、後日の同一契約による再実行を許可する。処理時間とPeak Working Setは観測値として記録するが、Tier合否、Geometry hash、Dataset hashの入力には使用しない。

##### Canonical Selection Report／Licensed Dataset Index／Receipt

`EarlyFixtureSelectionProfile`、`EarlyFixtureSourceCatalog`、`CanonicalBundleIndex`、`EarlyFixtureSelectionReport`、`LicensedRepresentativeDatasetIndex`、`LicensedFixtureSelectionReceipt`は独立したSchema Version、canonical UTF-8 JSON Codec、content SHA-256を持つ。共通規則はBOMなし、余分な空白／末尾改行なし、固定property順、未知property禁止、nullable propertyも省略せずJSON `null`、hashは小文字64桁、浮動小数点は有限・負の0を0へ正規化した最短round-trip表現とする。

`EarlyFixtureSourceCatalog` v1はImport処理より前に作り、投入母集団と匿名IDを固定する。root property順は`SchemaVersion`、`CatalogId`、`EntryCount`、`Entries`とし、SchemaVersionはinteger `1`、CatalogIdは`[A-Za-z0-9._-]{1,128}`、EntryCountは1..100000かつ配列長と一致する。Entryは`SourceFixtureId`のordinal順で、property順を`SourceFixtureId`、`AssetCategory`、`SourceRelativePath`、`SourceFileSha256`とする。SourceFixtureIdはCatalog内で一意な`[A-Za-z0-9_-]{1,64}`の匿名ID、AssetCategoryはProfileの許可値、SourceRelativePathは後述のSource Bundle Indexに存在する正規化相対path、SourceFileSha256はそのfile bytesのSHA-256とする。これによりBlender起動／FBX Importに失敗してTriangle数を得られなくても、投入Source、カテゴリ、入力file hashをReportへ復元できる。

Source／Script／Preset bundleはarchive file自体やdirectory timestampをhashせず、展開済みtreeから作るcanonical `CanonicalBundleIndex` v1で識別する。root property順は`SchemaVersion`、`BundleKind`、`EntryCount`、`Entries`、SchemaVersionはinteger `1`、BundleKindは`Source`／`Script`／`Preset`、EntryCountは1..100000かつ配列長と一致する。各Entryのproperty順は`RelativePath`、`ByteLength`、`ContentSha256`とし、RelativePathのUTF-8 byte列によるordinal昇順、ByteLengthは0..2147483647、ContentSha256はfile bytesの小文字64桁SHA-256とする。

RelativePathはbundle rootからの相対pathをUnicode NFCへ正規化し、separatorを`/`へ統一する。空path、先頭`/`、drive／UNC prefix、末尾`/`、空segment、`.`／`..` segment、NUL／control文字、backslashをRejectし、正規化後の完全一致とUnicode simple case-fold後の衝突をともにRejectする。通常fileだけを列挙し、symlink、junction、reparse point、device、socket等はRejectする。空directory、directory名、timestamp、ACL、所有者、archive圧縮方式はIndexへ含めない。CanonicalBundleIndex artifact自体はindexed rootの外へ出力し、自己参照Entryへ含めない。file bytesは変換せずそのままhashし、Indexのcanonical bytesのSHA-256をBundle Content SHA-256とする。同じ展開file集合ならZIP等のcontainer bytesや展開時刻が違っても同じbundle hashになる。

Source Bundleにはcanonical Source Catalog bytesを予約path`metadata/early_fixture_source_catalog.v1.json`の通常Entryとして必ず含め、Catalogが参照する全SourceRelativePathとSourceFileSha256をBundle Indexへ1対1照合する。Catalog外の補助fileをSource Bundleへ含めてもよいが、選抜対象SourceはCatalog Entryだけとする。`SourcePackageContentSha256`、`ScriptBundleContentSha256`、`PresetBundleContentSha256`は、それぞれBundleKindが一致するCanonicalBundleIndex bytesのSHA-256であり、Report／Index Codecは参照Indexを再hashして一致を検証する。これによりSourceFixtureIdとAssetCategoryの対応、Script、Presetの算出対象がすべてhashへ閉じる。

`CanonicalBundleVerifier`は既存Bundle Indexと明示された対応rootを受け取り、Index生成時と同一規則でrootを再帰列挙する。symlink／junction／reparse point等をRejectし、正規化した通常file path集合がIndex EntryのRelativePath集合と完全一致することを要求する。欠落file、Indexにない余分な通常file、path重複／case-fold衝突をRejectし、各fileの実byte長とraw bytes SHA-256をByteLength／ContentSha256へ照合する。Index artifact自体はroot外にあることを要求し、探索順、mtime、archive bytes、キャッシュ済みhashだけで検証を省略しない。

Phase 0.2 HarnessはBlenderを起動する前にSource／Script／Presetの3 rootをそれぞれVerifierへ通し、その時点の3 Bundle Index content hashをSelection Runへ固定する。Report／Dataset Index生成後、Receiptを確定する直前に同じ3 rootと同じIndex bytesでもう一度完全照合し、file集合、長さ、内容またはIndex hashが開始時から変化していればRun全体をRejectしてReceiptを作らない。Report／Index CodecによるIndex bytesの再hashはこの実tree照合の代替ではなく、Receipt確定済みRunの再利用時も、対応rootが提供される処理ではVerifier合格を必須とする。

Report v1のproperty順は`SchemaVersion`、`SelectionRunId`、`ProfileContentSha256`、`SourcePackageContentSha256`、`BlenderVersion`、`BlenderExecutableSha256`、`ScriptBundleContentSha256`、`PresetBundleContentSha256`、`HostProfileId`、`DatasetIndexContentSha256`、`EntryCount`、`Entries`とする。`SelectionRunId`は小文字UUID、各version／ID stringはTrim済み1..128文字、`DatasetIndexContentSha256`はDatasetを確定できた場合だけhash、それ以外は`null`とする。Entriesは`SourceFixtureId + Tier + GeometryProcessMode + VariantId`のordinal順で並べ、EntryCountは0..100000かつ配列長と一致する。

ReportはSource Catalogの全SourceFixtureIdを少なくとも1 Entryで被覆する。Blender Processを開始できない場合は`Launch`、Process開始後に固定Script／Presetの初期化、version検証、引数検証へ失敗してImportへ到達しない場合は`Bootstrap`、Source fileの読込／FBX解析失敗は`Import`として区別する。これらによりVariant展開へ到達しなかったSourceには、`Tier=Render`、`GeometryProcessMode=Original`、`VariantId=original`の決定的な失敗Entryを1件作り、Status／Attemptへ実際のStageとToolまたはResource失敗を記録する。開始したVariant試行は成功・失敗を問わずそれぞれ固有Entryを持たせ、後続失敗をSource Catalogや成功Entryだけで代用しない。CatalogにないSourceFixtureIdをReportへ追加することは禁止する。

各Report Entryのproperty順と型は次に固定する。

| Entry property | JSON型 | 契約 |
| --- | --- | --- |
| `SourceFixtureId` | string | Catalogと同じ匿名化した`[A-Za-z0-9_-]{1,64}` |
| `SourceGeometrySha256` | string | Source CatalogのSourceFileSha256と一致する小文字64桁。Import失敗時もSource file bytesから取得可能 |
| `AssetCategory` | string enum | `Furniture`／`Vehicle`／`Building`／`RoadEquipment`／`SmallProp`／`Character`／`Other`。Source内で不変 |
| `SourceTriangleBand` | string enum／null | `UpTo100`／`From101To500`／`From501To1000`／`From1001To2000`／`From2001To5000`／`Over5000`。Triangle数取得前のLaunch／Bootstrap／Import失敗時だけ`null` |
| `Tier` | string enum | `Render`／`Solid`／`Convex` |
| `GeometryProcessMode` | string enum | `Original`／`DirectDecimate`／`VoxelRemesh`／`VoxelPostDecimate`／`ConvexBuild` |
| `VariantId` | string | 同じSourceFixtureId＋Tier内で一意な`[A-Za-z0-9._-]{1,48}` |
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
| `VolumeError` | number／null | Solid／ConvexだけSource比 |
| `SurfaceDistanceP95` | number／null | Render／SolidのSource diagonal比 |
| `BoundaryEdgeCount` | integer／null | 検査完了時は0以上 |
| `NonManifoldEdgeCount` | integer／null | 検査完了時は0以上 |
| `OrientationMismatchEdgeCount` | integer／null | 検査完了時は0以上 |
| `SelfIntersectionCandidatePairCount` | integer／null | Solid broad phase完了時は`0..MaxSolidCandidatePairCount`。上限超過時は検出時点の`MaxSolidCandidatePairCount + 1` |
| `SelfIntersectionCount` | integer／null | 検査完了時は0以上 |
| `ConvexHullCount` | integer／null | Convex時だけ0以上 |
| `ConvexTotalVertexCount` | integer／null | Convex時だけ0以上 |
| `AttemptCount` | integer | `1..2` |
| `Attempts` | object array | `AttemptCount`件。AttemptOrdinal昇順、最大2件。下記固定schema |
| `RejectStage` | string enum | `None`／`Launch`／`Bootstrap`／`Import`／`Normalize`／`ProfileGuard`／`RenderGate`／`SolidGate`／`ConvexGate`／`VoxelRemesh`／`Decimate`／`CanonicalGeometry`／`ResourceGuard`／`Export` |
| `RejectReason` | string enum | `None`／`NonFinite`／`DegenerateBounds`／`DegenerateTriangle`／`Boundary`／`NonManifold`／`Orientation`／`SelfIntersection`／`BoundsDeviation`／`VolumeDeviation`／`ConvexLimit`／`InputLimit`／`OutputLimit`／`VoxelCellLimit`／`CandidatePairLimit`／`Timeout`／`MemoryLimit`／`ToolFailure` |

各Attemptのproperty順は`AttemptOrdinal`、`AttemptStatus`、`ProcessMilliseconds`、`PeakWorkingSetBytes`、`ToolExitCode`、`RejectStage`、`RejectReason`とする。`AttemptOrdinal`は1始まりの連番、`AttemptStatus`は`Succeeded`／`ResourceLimitExceeded`／`ToolFailed`、時間は有限の0以上、Peakは0以上のinteger、`ToolExitCode`はProcessが終了codeを返した場合だけsigned 32-bit integer、それ以外は`null`とする。`Succeeded`ではAttemptのReject Stage／Reasonを`None`、資源超過またはTool失敗では該当Stage／Reasonを必須とする。Entryの最終Reject Stage／Reasonは最終分類結果、Attempts内は各実行結果を表し、相互に上書きしない。`AttemptCount == Attempts.length`を要求し、2件目は1件目が`ResourceLimitExceeded`の場合だけ許可する。初回超過後に成功したEntryは`AttemptCount=2`、Attemptsが`ResourceLimitExceeded`、`Succeeded`の順となる。

最終Entry StatusとAttempt列の許可組合せは次の完全決定表に固定し、表にない組合せをCodecでRejectする。角括弧内はAttemptStatusの順序である。

| 最終Entry Status | 許可Attempt列 | Entry最終Reject Stage／Reason |
| --- | --- | --- |
| `Selected`／`BenchmarkOnly`／`NoOp`／`Alias` | `[Succeeded]`または`[ResourceLimitExceeded, Succeeded]` | `None／None` |
| `GeometryRejected` | `[Succeeded]`または`[ResourceLimitExceeded, Succeeded]` | Geometryを棄却した実Stageと、`NonFinite`から`ConvexLimit`までの該当Geometry Reason。資源／Tool Reasonは禁止 |
| `ProfileUnsupported` | `[Succeeded]`または`[ResourceLimitExceeded, Succeeded]` | `ProfileGuard`と`InputLimit`／`OutputLimit`／`VoxelCellLimit`／`CandidatePairLimit`のいずれか |
| `ResourceLimitExceeded` | `[ResourceLimitExceeded]`だけ | `ResourceGuard`と`Timeout`または`MemoryLimit`。再試行待ちの中間Reportだけで許可 |
| `ResourceDeferred` | `[ResourceLimitExceeded, ResourceLimitExceeded]`だけ | `ResourceGuard`と2件目の`Timeout`または`MemoryLimit` |
| `ToolFailed` | `[ToolFailed]`または`[ResourceLimitExceeded, ToolFailed]` | Tool失敗が発生した実Stageと`ToolFailure` |

Attempt単位の`ResourceLimitExceeded`は超過が実際に発生した`Launch`／`Bootstrap`／`Import`／`Normalize`／`RenderGate`／`SolidGate`／`ConvexGate`／`VoxelRemesh`／`Decimate`／`CanonicalGeometry`／`Export`のいずれかと、Reason `Timeout`／`MemoryLimit`を持つ。Attempt単位の`ToolFailed`も失敗が起きた実StageとReason `ToolFailure`、`Succeeded`は`None／None`だけを許可する。Entry全体のResource Statusだけは最終Stageを`ResourceGuard`へ集約し、最終Reasonを末尾Attemptと一致させる。2件目は常に最終Attemptであり、ToolFailed後のretry、Succeeded後のretry、3件目を禁止する。最終Statusが`ResourceLimitExceeded`の未完了Reportでは`DatasetIndexContentSha256=null`とし、Dataset Index／Receiptを確定してはならない。これにより`Selected + ToolFailed`、1 Attemptの`ResourceDeferred`、`GeometryRejected + ResourceLimitExceeded`等を表現不能にする。

同じ`SourceFixtureId`を持つ全EntryはSource Catalogと同一の`SourceGeometrySha256`／`AssetCategory`を持ち、そのカテゴリがProfileの`AssetCategories`に含まれることを要求する。`SourceTriangleCount`が非nullなら`SourceTriangleBand`も必須で、Profileの`SourceTriangleBandUpperBounds`からCodecが再計算して一致を検証する。両方の`null`は、全Attemptが`Launch`／`Bootstrap`／`Import`のいずれかでGeometry取得前に失敗し、最終Statusが`ToolFailed`、`ResourceLimitExceeded`または`ResourceDeferred`の場合だけ許可する。この場合、Triangle依存の形状統計とReductionRatioも`null`にする。片方だけの`null`、Geometry取得後の`null`、不明値を0として保存することは禁止する。カテゴリはSource Catalogで固定し、処理成否やVariant結果から後付け変更しない。

`VariantId`の一意keyは`SourceFixtureId + Tier + VariantId`とし、Tier間では同じVariantIdを許可する。Selected／BenchmarkOnlyの`DatasetCaseId`は上記Tier付き構築式と厳密一致し、Index全体で重複してはならない。`CanonicalVariantId`によるNoOp／Alias参照も同じSourceFixtureId＋Tier内だけに限定し、RenderとSolidなどTierをまたぐAlias化や参照を禁止する。

`NoOp`と`Alias`は新しいDatasetCaseを作らず、`DatasetCaseId=null`とし、`CanonicalVariantId`で既存のcanonical Variantへ対応させる。参照先がSelected／BenchmarkOnlyとして存在しない場合はNoOp／Aliasにせず、基底と同じ失敗Status／Reasonを記録する。Geometry Reject、ProfileUnsupported、Resource状態、ToolFailedもDataset Indexへ入れない。

`LicensedRepresentativeDatasetIndex` v1のproperty順は`SchemaVersion`、`DatasetId`、`ProfileContentSha256`、`SourcePackageContentSha256`、`BlenderVersion`、`BlenderExecutableSha256`、`ScriptBundleContentSha256`、`PresetBundleContentSha256`、`VariantCount`、`Variants`とする。SchemaVersionはinteger `1`、DatasetIdは`[A-Za-z0-9._-]{1,128}`、VariantsはDatasetCaseIdのordinal順で並べ、DatasetCaseIdはIndex内で一意、VariantCountは1..100000かつ配列長と一致する。各Variantのproperty順は`DatasetCaseId`、`SourceFixtureId`、`Tier`、`GeometryProcessMode`、`VariantId`、`QualityClass`、`GeometryFormat`、`GeometryFormatVersion`、`GeometryRelativePath`、`GeometryByteLength`、`GeometryContentSha256`、`SourceGeometrySha256`、`SourceTriangleCount`、`ActualInputTriangleCount`、`ReductionTargetTriangleCount`、`VoxelResolutionCells`、`PostReductionTargetTriangleCount`とする。GeometryFormatはstring enum `ZantetsuCanonicalGeometry`、GeometryFormatVersionはinteger `1`、QualityClassは`Representative`／`BenchmarkOnly`、GeometryByteLengthは16..67108864、Triangle／Voxel countsは0以上のinteger、nullableなTarget／Voxel propertyは非該当時に明示`null`とする。

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

Blender評価Meshのface loopは、`inverse(M_root) * M_object`の線形成分が負determinantならObject transform Bake時に1回だけ反転し、Bake後のBlender右手系で評価時のfront-facingを保つ。Solid／Convexはその後に外向きCCWへOrientation Gateで統一し、開放Renderは評価時の向きを保つ。`C`のdeterminantは`-1`なので、Blender右手系のCCW loopはindex順を追加反転せずZCG左手系の外向きclockwise loopになる。TriangulationはTransform Bake、負determinant補正、Solid向き統一の後、`C`適用前に行う。

変換後floatはround-to-nearest-ties-to-evenでbinary32化し、NaN／InfinityをReject、負の0を正の0へ正規化する。Headerは4 byte ASCII magic `ZCG1`、1 byte `GeometryKind`（`1=TriangleMesh`、`2=ConvexSet`）、3 byte zero reserved、8 byte unsigned payload lengthの計16 byteとし、宣言長はfile長から16を引いた値と厳密一致させる。可変padding、末尾data、未知Kind、非zero reservedをRejectする。

ZCGの全幾何判定は、格納対象の正規化済みbinary32 positionをbinary64へ正確に拡張した値だけを正本とする共通`ZcgNumericKernelV1`を使う。Blender側の元double座標、Normal、既存Plane、Unity側float計算を判定へ混ぜない。演算はIEEE 754 binary64 round-to-nearest-ties-to-even、FMA／fast-math無効、積と差を式の記載順、dotと総和を左畳みで行う。`dot(a,b) = ((a.x*b.x + a.y*b.y) + a.z*b.z)`、`crossRH(a,b) = (a.y*b.z-a.z*b.y, a.z*b.x-a.x*b.z, a.x*b.y-a.y*b.x)`、`length(c) = sqrt(((c.x*c.x + c.y*c.y) + c.z*c.z))`へ固定し、sqrtはIEEE 754 correctly-rounded binary64を使用する。

検証対象domainのbinary32 positionから各軸min／maxをpositionのcanonical順に比較して求め、軸差を`dx,dy,dz`とする。`D = sqrt(((dx*dx + dy*dy) + dz*dz))`、`epsDistance = max(Profile.AbsoluteEpsilonMeters, D * Profile.RelativeEpsilon)`、`epsArea = epsDistance * epsDistance`、`epsVolume = epsArea * epsDistance`とする。Dが非正／非有限ならRejectする。距離／半空間誤差はepsilon以下を包含側とする一方、非退化面積と正体積はそれぞれ`> epsArea`、`> epsVolume`を必須とし、等号は退化側としてRejectする。

`TriangleMesh` payloadは`uint32 PositionCount`、`uint32 TriangleCount`、続いてPositionCount件の`float32 x,y,z`、TriangleCount件の`uint32 i0,i1,i2`とする。元Geometryを位置だけのtriangle soupへ展開し、完全に同じ正規化positionを1件へweldして、positionを数値`x,y,z`のlexicographic昇順へ並べ直す。各Triangleは新indexへremapし、windingを反転せず3 indexをcyclic rotationして辞書順最小表現にし、Triangle列全体を`i0,i1,i2`の辞書順へsortする。範囲外index、同一頂点を含むTriangle、同一index tripleの重複をRejectする。Render／Solid TierはこのKindを使う。

Triangle退化判定のdomainはTriangleMesh全体とし、上記domain Boundsからepsilonを1回だけ計算する。各Triangleについて`u=v1-v0`、`w=v2-v0`、`twiceArea = length(crossRH(u,w))`を記載順binary64で計算し、`twiceArea > epsArea`だけを合格とする。`twiceArea == epsArea`とそれ未満はRejectし、binary32 positionの1 ULP差で境界をまたぐ場合もこの比較結果をそのまま使用する。実面積へ0.5を掛けてから比較したり、TriangleごとのBounds、Blender double、Unity float、近似Normal長を使ってはならない。

座標変換のGolden Fixtureは`M_root=identity`、`M_object=translation(10,20,30)`、`s=0.5`、Blender local triangle `[(1,2,3),(4,6,5),(-2,7,11)]`を入力とする。ZCG変換、position sort、triangle cyclic rotation後はpositions `[(4,20.5,13.5),(5.5,16.5,11),(7,17.5,13)]`、triangle `[0,1,2]`、payload length 56、file length 72でなければならない。完成fileのhexは`5a4347310100000038000000000000000300000001000000000080400000a441000058410000b04000008441000030410000e04000008c4100005041000000000100000002000000`、SHA-256は`5210748ea4fe7a8f349b52e919af7dd1aad4c542a91fb741806bf517f2426cdbf`へ固定する。

`ConvexSet` payloadは`uint32 HullCount`の後にHull recordを連結する。各Hull recordは`uint32 PositionCount`、`uint32 FaceCount`、position列、各Faceの`uint32 IndexCount`とindex列からなる。Hull内positionはTriangleMeshと同じ規則でweld／sort／remapする。Face loopは外向きwindingを維持したままcyclic rotationで辞書順最小化し、Face列をIndexCountとindex列の辞書順へsortする。各Hullを一時canonical bytesへserializeし、そのbytesのunsigned byte lexicographic昇順でHull recordをsortする。Convex TierはこのKindを使う。

Convexの検証domainはHullごととし、各Hullのbinary32 position Boundsから`ZcgNumericKernelV1`で`epsDistance`／`epsArea`／`epsVolume`を独立に計算する。比較境界と演算精度は共通Kernelから変更しない。

各Faceはcanonical rotation後の`v0`を固定し、`i=1..IndexCount-2`の順に`c = -crossRH(v[i]-v0, v[i+1]-v0)`を計算して、`length(c) > epsArea`となる最初のtripletをPlane生成へ使う。存在しなければFaceを退化としてRejectする。`n = c / length(c)`、`d = -(((n.x*v0.x + n.y*v0.y) + n.z*v0.z))`とし、このPlaneをそのpolygon faceの唯一の解釈とする。Face全頂点で`abs((((n.x*v.x + n.y*v.y) + n.z*v.z) + d)) <= epsDistance`を要求し、非平面polygonをepsilon内だけ許可する。Hull全頂点について同じ値が`<= epsDistance`であることを要求し、1点でも正側へ超過したHullを非凸または内向きFaceとしてRejectする。

Topologyは各FaceのIndexCount 3以上、範囲内でFace内重複indexなし、重複Faceなしを要求し、各undirected edgeがちょうど2 Faceに現れてdirected向きが互いに逆であることを閉鎖条件とする。Hull bounds centerを`r`とし、canonical Face順と各Faceのfan順で`V = left_sum(-dot(v0-r, crossRH(v[i]-r, v[i+1]-r)) / 6)`を計算する。ZCGのclockwise外向き規約では`V > epsVolume`を必須とし、`V <= epsVolume`、負volume、非有限volumeをRejectする。Face半空間、閉鎖edge、正volumeの全条件を通ったものだけをConvexとして扱う。3未満のFace index、ProfileのHull／Vertex／Face上限超過もRejectする。

ZCG Encoderは同じ正規化Geometryから常に同じbytesを生成し、`GeometryContentSha256`は完成ZCG file bytes全体のSHA-256とする。Alias判定も同じSourceFixtureId＋Tier内のこのhashで行う。Verifier／Benchmark LoaderはIndexのFormat／VersionでDecoderを選び、decode後に同じEncoderで再serializeしたbytesが入力fileとbyte-for-byte一致しなければnon-canonicalとしてRejectする。これにより元Triangle／Vertex／Hullの列挙順、FBX metadata、container timestampはGeometry hashへ影響せず、位置、winding、Topologyの変化だけがcanonical bytesへ反映される。

全VariantはZCG encode後にfileをDecoderで読み直し、decodeされたbinary32 positionとcanonical indexだけを入力として最終Gateを再実行する。Render Tierはfinite、Bounds、Triangle退化／重複、Profile Triangle／Component上限を再検証する。Solid Tierはそれらに加え、undirected edge key `(min(i0,i1), max(i0,i1))`をcanonical Triangle順で構築し、出現1回をBoundary、3回以上をNon-Manifold、2回でもdirected向きが逆でないものをOrientation不整合として数え、すべて0を要求する。binary32 weld後のTriangle edge adjacencyから連結成分を再構築し、成分はその成分が含む最小canonical Triangle indexの昇順、成分内Triangleはglobal canonical Triangle順を保つ。

Solidのsigned volumeは成分ごとに次の`SolidSignedVolumeV1`だけで計算する。成分で参照されるpositionをcanonical position index順に走査してbinary64のcomponent Bounds `min`／`max`を求め、参照点を各軸について`r = min + (max - min) * 0.5`の順で計算する。Triangle `(v0,v1,v2)`ごとに`a=v0-r`、`b=v1-r`、`c=v2-r`、`q=crossRH(b,c)`、`numerator=-dot(a,q)`、`term=numerator/6.0`をこの順にbinary64で評価する。`V0=+0.0`から成分内canonical Triangle順に`Vk+1=Vk+termk`を左畳みし、除算後のtermだけを加算する。式の再結合、原点基準への置換、pairwise／Kahan加算、FMA、除算の後回しは禁止する。成分Boundsから共通Numeric Kernelで算出した`epsVolume`に対し、有限な`V > epsVolume`だけを合格とし、`V == epsVolume`を含む`V <= epsVolume`、負値、非有限値をRejectする。Report用の全体Volumeは成分順に各合格`V`を同じbinary64左畳みで加算し、途中または最終値が非有限ならRejectする。

`SolidGeometryValidatorV1`はZCG bytesを入力とするversion固定の共有Validatorを唯一の正本とし、Profileの`SolidIntersectionAlgorithm`は`ClosedTriangleDistanceV1`だけを許可する。Phase 0.2のBlender HarnessはPython独自predicateを実装せず、ZCG encode後にScript Bundleへhash固定された共有Validatorを呼び出す。Unity Editor側のDataset検証とT-081も同じValidator artifactを使用する。実装artifact、CLI引数、終了codeは`ScriptBundleContentSha256`の対象とし、利用不能・version不一致・未知algorithmを`ToolFailed`として扱い、別ライブラリへFallbackしない。事前Solid Gateの結果を流用したり、元Blender doubleで再判定したりしない。

`ClosedTriangleDistanceV1`はbinary32から正確にbinary64へ展開した2つの閉Triangle間の最小二乗距離を決定論的に求める。候補は、Aの3頂点から閉Triangle Bへのpoint-triangle二乗距離、Bの3頂点から閉Triangle Aへの同距離、Aの3 closed edgeから閉Triangle Bへのsegment-triangle二乗距離、Bの3 closed edgeから閉Triangle Aへの同距離、AとBの各3 edgeによる9組のclosed-segment間二乗距離の順とし、各群内はlocal vertex／edge番号の辞書順で評価する。segment-triangleはsegmentとTriangle planeの交点parameterが閉区間`[0,1]`にある場合、固定式で`u`、`v`、`w=1-u-v`の順にbinary64 barycentricを計算し、`u >= 0 && v >= 0 && w >= 0`ならface interior／boundary貫通として距離0のwitnessを返す。等号は包含し、比較不能／非有限ならこの0距離分岐を採用せず後続の保守的距離候補へ進む。非平行時のplane交点、平行／coplanar時の3 edgeとのsegment-segment、両endpointのpoint-triangle候補を固定順に評価するため、「一方のedgeが他方のface内部を貫通するが頂点もedge同士も接触しない」proper crossingも検出する。

point-triangle、segment-triangle、segment-segmentはversion固定のEricson型region testを、`ZcgNumericKernelV1`のbinary64演算順、`dot`、`crossRH`、除算、clampへ逐語的に固定した共有実装とする。各候補は二乗距離だけでなく両Triangle上のclosest witness `(pA,pB)`と各Triangleのbarycentricを返す。barycentric値およびsegment parameterの`0`と`1`は閉区間へ含め、clampは`x < 0 ? 0 : (x > 1 ? 1 : x)`、候補minimumはstrict `<`の場合だけ更新して同値なら先の候補を保持する。退化Triangleは先行Triangle Gateで、zero-length edgeまたは非有限な分母はTopology／退化Rejectで到達不能とし、predicate内で別形状へ降格しない。`epsDistanceSquared=epsDistance*epsDistance`もbinary64でこの順に一度だけ計算する。

自己交差候補は全`TriangleCount choose 2`を走査せず、version固定の`SolidCandidateBvhV1`で生成する。各Triangleのbinary64 AABBを各軸の正負へ`epsDistance`だけ拡張し、非有限化またはdomain Boundsを越える算術overflowをRejectする。primitive初期順はcanonical Triangle index順とし、各nodeでTriangle centroid Boundsのextentが最大の軸をsplit axisに選ぶ。同値はX、Y、Z順、軸上のstable sort keyは`centroid[axis]`のbinary64 total-order、次にcanonical Triangle indexとする。個数`n`のnodeは`floor(n/2)`で左右へ分割し、leafは1 Triangle、node IDはpreorderで付与する。比較、Bounds union、中央値、node作成順をこの規則から変更せず、SAHや並列schedule順をcanonical結果へ使わない。

候補生成はroot対rootから始める。同一node pairでは`(left,left)`、`(left,right)`、`(right,right)`、異なるnode pairではAABBが全3軸で閉区間交差する場合だけ下降する。両方leafなら`a < b`へ正規化してpairを出力し、片方だけ内部nodeならその左右を順に、両方内部nodeならprimitive数の多い側を分割し、同数ならnode IDの大きい側を分割する。この規則により各unordered leaf pairを最大1回だけ生成するが、出力後もuint32 `(a,b)`のradix sortで昇順へ正規化し、隣接重複を除去してから狭域判定へ渡す。重複の有無を診断値へ残し、重複があってもdeduplicate後の意味は変えない。

候補counter、node数、byte数はchecked unsigned 64-bitで配列確保前とappend前に検査する。一意候補がProfileの`MaxSolidCandidatePairCount=2000000`へ達した後、次の異なるpairを検出した時点で追加割当や狭域判定を行わず、Reportの`SelfIntersectionCandidatePairCount`を`MaxSolidCandidatePairCount + 1`、最終Statusを`ProfileUnsupported`、RejectStageを`ProfileGuard`、RejectReasonを`CandidatePairLimit`として終了する。候補counter、`2 * TriangleCount - 1`のnode数、pair／node byte長のいずれかがchecked overflowする場合も、割当前に同じsentinelと`ProfileUnsupported／ProfileGuard／CandidatePairLimit`へ収束させる。Triangle AABBのepsilon拡張だけが非有限化した場合はGeometry入力の`NonFinite`として`GeometryRejected／CanonicalGeometry`にする。これらは決定論的入力複雑度または数値上限でありResource retryしない。上限以内でも実際の120／300秒または4 GiBを超えた場合だけ既存の`ResourceLimitExceeded`／`ResourceDeferred`へ移す。

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

したがって「Triangle interiorだけ」という別predicateは持たず、Topologyで共有されたedge／vertexのepsilon近傍だけを明示的に許可する。共有index数だけを根拠にpair全体を除外してはならない。共有indexなしのpair、または共有simplex許可領域外の残余接触ではTriangle間距離がepsilonちょうどなら自己交差、binary64でその直外なら非交差とする。共有simplexからの距離がepsilonちょうどの正常接触は閉じた`N(S)`内として許可する。候補pairをcanonical順に処理した件数がProfileの`MaxSelfIntersectionCount=0`以下であることを要求する。

ZCG後GateでBoundary、Non-Manifold、向き、自己交差、成分volume、Bounds、Triangle退化のいずれかが失敗したVariantは最終Status `GeometryRejected`、RejectStage `CanonicalGeometry`、対応する既存Geometry RejectReasonとし、DatasetCaseIdを付与せずDataset Index／Receiptへ含めない。ReportのActualOutputTriangleCount、連結成分、Bounds、Volume、Boundary／Non-Manifold／SelfIntersection Candidate Pair／SelfIntersection統計は合格・不合格ともZCG decode後の値で上書きし、canonical化前の値を最終統計として残さない。Candidate Pair上限超過だけは完全列挙せず、規定のsentinel `MaxSolidCandidatePairCount + 1`を保存する。

ZCG v1のschema byte上限は64 MiBとし、Decoderはそれ以下の呼び出し側`maxBytes`を必須とする。HeaderとIndexのGeometryByteLengthを配列確保前に照合し、TriangleMeshはTriangleCountをProfileのMaxVariantTriangleCount以下、PositionCountをその3倍以下、ConvexSetはHull／Vertex／Face数をProfileのConvex上限以下へ制限する。全record長はchecked 64-bit算術でpayload長と突き合わせ、overflow、宣言数過剰、途中EOFをRejectしてからだけ配列を確保する。未知Format／Versionを別形式として推測decodeせずRejectする。

`GeometryRelativePath`はGeometry Dataset rootからの相対pathで、CanonicalBundleIndexと同じNFC、`/` separator、segment、control文字、case-fold衝突、通常file限定の規則を適用し、拡張子を小文字`.zcg`へ固定する。各Variantは異なるGeometryRelativePathを持ち、Index／Report／Receipt artifactはGeometry Dataset rootの外へ保存する。directory階層は固定しないが、pathはIndexのcanonical identityに含め、DatasetCaseId変更時に暗黙で使い回さない。

Index Codecは各Variantを最終Report内の同じDatasetCaseIdを持つSelected／BenchmarkOnly Entryへ厳密に1対1対応させ、Tier付きDatasetCaseId構築式、Process、VariantId、Source／Geometry hash、Source／Actual Triangle、Target／Voxel property、QualityClassが一致することを検証する。Verifierは明示されたGeometry Dataset rootを再帰列挙し、symlink／junction／reparse point等をRejectして、正規化した通常file path集合がIndexのGeometryRelativePath集合と完全一致することを要求する。欠落file、Indexにない余分な通常file、path重複／case-fold衝突をRejectし、各fileの実byte長をGeometryByteLength、raw bytesのSHA-256をGeometryContentSha256へ照合する。探索順や拡張子推測で対象fileを選ばない。ReportだけにあるNoOp／Alias／失敗／Resource EntryはIndex件数へ含めない。

`DatasetContentSha256`はcanonical `LicensedRepresentativeDatasetIndex` bytesそのもののSHA-256とし、後続`GeometryBenchmarkRunManifest`へ同じ`DatasetId`とともに格納する。変動するAttempt時間、Peak Working Set、HostProfileId、Report hashはDataset Indexへ含めないため、同じGeometry集合とTool／Profile hashなら実行時間が変わってもDataset hashは変化しない。

最終的な双方向監査は小さなcanonical `LicensedFixtureSelectionReceipt` v1で閉じる。property順は`SchemaVersion`、`SelectionRunId`、`DatasetId`、`ReportContentSha256`、`DatasetIndexContentSha256`、`DatasetContentSha256`とし、SchemaVersionはinteger `1`、SelectionRunIdはReportと同じ小文字UUID、DatasetIdはIndexと同じID、3 hashは小文字64桁とする。`DatasetIndexContentSha256 == DatasetContentSha256`を要求し、Report bytesとIndex bytesを再hashして両Content hashへ照合し、Report内のSelectionRunId／DatasetIndexContentSha256とIndex内のDatasetIdも一致させる。ReceiptはReportとIndexの両方がcanonical検証に合格した後、最後に原子的に確定するcommit markerであり、欠落または不一致ならその選抜Runを未確定としてBenchmarkへ渡さない。これによりDataset hashは時間情報から独立したまま、失敗EntryやAttempt履歴を含む特定Reportを特定Indexへ固定できる。

canonical Loaderのschema上限はProfile 64 KiB、Source Catalog 16 MiBかつ100000 Entry、各Canonical Bundle Index 16 MiBかつ100000 Entry、Report 64 MiBかつ100000 Entry／合計200000 Attempt、Dataset Index 64 MiBかつ100000 Variant、Receipt 64 KiBとする。すべてのLoaderは`maxBytes`を、Catalog／Bundle／Index Loaderは`maxEntries`を、Report Loaderは`maxEntries`と`maxAttempts`を呼び出し側から必須で受け取り、各値が0より大きく対応するschema上限以下でなければ呼出し自体をRejectする。無制限overloadやschema上限だけを暗黙使用するpublic APIは設けない。

Loaderは、(1) seek可能入力なら配列確保前に総byte長をschema上限と呼び出し側上限の小さい方へ照合する。非seek入力では有効limitを`min(schemaMaxBytes, maxBytes)`とし、最大`limit + 1` byteまで試読して、Parser bufferへ保持するのは先頭limit byteまでとする。`limit + 1`番目を1 byteでも取得した時点でSizeLimitExceededとしてRejectし、そのbyteをJSON parserやhashへ渡さない。ちょうどlimit byteでEOFなら受理可能とする。(2) JSON nesting最大8、単一string token最大1024 UTF-8 byte、property数を各固定schemaへ制限、(3) SchemaVersionと固定root property順を検証、(4) 宣言Entry／Variant／Attempt件数をschema上限と呼び出し側上限へ照合、(5) その後だけ配列を確保、(6) 全要素、実配列長、ordinal順、末尾dataなしを検証、の順で処理する。Reportの`AttemptCount`合計も`min(200000, maxAttempts)`以下かつ実Attempts総数と一致させる。Receipt Loaderは参照先を自動で無制限読込せず、検証側が各参照文書用の個別上限を明示して読み込む。

### 10.3 Blenderヘッドレス前処理

Blenderを手作業用DCCだけでなく、ライセンスAssetをローカル変換するバッチプロセッサとして使用する。システムに既存のBlenderやPATH上の`blender`には依存せず、プロジェクト専用の固定版を明示パスから`--background --factory-startup --python --python-exit-code 1`で起動する。PythonスクリプトとAsset別RecipeからSolid Cut Mesh、Physics Proxy、検証レポートを生成する。

```text
Licensed Display Asset
  -> Import／Transform・単位統一
  -> 部品分類と不要装飾の除外
  -> 指定開口の封鎖
  -> Voxel化・内部充填
  -> Watertight Mesh化
  -> Trusted Exteriorへの制約付きSurface Projection
  -> Projection後の幾何検証
  -> 簡略化・三角形化
  -> Solid／Physics検証
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

### 10.5 片面・開放メッシュの自動修復

入力Assetが片面ポリゴン、底面欠落、微小隙間、自己交差を含む場合でも、以下の段階的処理でSolid Cut Mesh生成を試みる。形状修復と意味判断を分離し、大きな開口を無条件に封鎖しない。

```text
入力Mesh
  -> 重複頂点・退化面・孤立要素を除去
  -> Boundary Edge／Loop抽出
  -> 小さく平面的なLoopを自動封鎖
  -> 片面シェルへ分類別Solidify
  -> Voxel Union
  -> 小隙間のVoxel Closing
  -> 外部空間Flood Fillと内部充填
  -> Surface再生成
  -> Watertight・体積・表面偏差検証
```

#### 10.5.1 Boundary Loop封鎖

各Boundary Loopについて直径、周長、頂点数、平面からの最大誤差、周辺法線を測定する。共通Presetの閾値内にある小さく平面的なLoopだけを`holes_fill`または三角形化で封鎖する。大きい、非平面、分岐、他Loopと近接する境界は自動封鎖せずRecipeまたは`NeedsReview`へ送る。

初期候補として、直径10cm未満、平面誤差がVoxel Sizeの2倍以内、修復後の表面偏差がVoxel 1～2個以内を検証開始値とする。これは最終仕様ではなくT-027の実測で調整する。

#### 10.5.2 片面シェルのSolidify

閉じた体積を持たない一枚板は、法線規約とAsset分類別の厚みを使ってSolidifyする。法線は外向きを標準とし、厚みは原則として内側へ追加する。

| 分類 | 初期厚み候補 |
| --- | --- |
| WallPanel | 0.15m |
| Roof | 0.10m |
| CarBody | 0.04m |
| SignBoard | 0.02m |

厚み値はAsset寸法と画風に合わせてRecipeで上書きできる。法線が不整合、内外を決定不能、Solidify後に自己交差が残る場合は`NeedsReview`とする。

#### 10.5.3 Voxel Closingと内部充填

複数部品の微小なずれやEdge不一致はVoxel化後にUnionし、Voxel 1～3個を初期候補とするClosing半径で隙間を閉じる。その後、外部境界から到達可能なVoxelをFlood Fillし、到達不能領域を充填体として扱う。

Closing半径より小さい窓や溝も閉じる可能性があるため、車庫、トンネル、中庭、入口、窓など意味を持つ開口には`PreserveCavity`または封鎖マスクをRecipeで指定する。大開口を単純な大きさだけで自動判断しない。

#### 10.5.4 制約付きSurface Projection

Voxel／SDFは最終形ではなくTopology修復用の中間表現とする。比較的密なWatertight Surfaceを再構成した後、簡略化より先に元Assetの大きなSilhouetteと主要曲面を`Trusted Exterior`へ戻す。全頂点を無条件に最近傍面へShrinkwrapせず、次をすべて満たす頂点だけを投影する。

- 投影先TriangleがRecipeで許可された`Trusted Exterior`に属する。
- 移動距離がVoxel Sizeから導く最大距離以下である。初期検証候補はVoxel Cell対角長の0.5～1.5倍とする。
- Voxel側とTarget側の法線内積が閾値以上で、反対側の薄板、内部面、別部品へ飛び移らない。
- 合成封鎖面、内部充填面、`PreserveCavity`境界を元Assetの内部装飾へ戻さない。
- 必要外形の包含、最小厚み、符号付き体積を許容範囲内に維持する。

Blender標準ShrinkwrapのVertex Group、Distance Limit、Face Cull、Nearest Surface Point／Target Normal ProjectをPoC候補とする。ただし投影先部品ID、Normal条件、包含判定、自己交差回避が不足する場合はPython＋BVH Queryで制御する。Projection後に自己交差、面反転、退化、局所体積反転を検査し、失敗頂点は`投影量を縮小 -> Voxel位置へ復帰 -> Asset全体のProjection無効 -> NeedsReview`の順でFallbackする。

ReductionはProjection後に行い、Triangle数だけでなく元外表面距離、Normal変化、Silhouette、Sharp Featureを検査する。簡略化による収縮が問題になる場合だけ、より小さい距離と厳しい条件で最終再Projectionし、同じ幾何検証を再実行する。Solid Cut Meshは表示用ではないため、UV、Material、Tangentの転送を必須としない。

Topological Watertightは各Edgeが原則2面に接続することだけを意味し、自己交差のないSolidを保証しない。自己交差があってもShader `clip`はTriangle単位で動き、面向きが整合する限りWinding Count Stencilも非ゼロ領域を描ける場合がある。しかし実Mesh切断では平面交差線分が自己交差、重複、分岐して単純Loop前提を壊し、断面三角形化、反復切断、体積・重心・慣性、Convex検証を不安定にする。このため自己交差は即時表示専用Fallbackでは条件付き許容できても、StableなSolid Cut Mesh／Physics Proxyの合格条件には含めない。

#### 10.5.5 修復後の品質判定

生成結果は次の条件から`Success`、`NeedsReview`、`Failed`へ分類する。

- Boundary Edgeが0で、法線向きが整合している。
- 非隣接Faceの自己交差、面反転、幾何的重複がなく、Geometrically Valid Solidとして内外を一意に扱える。
- 符号付き体積が正かつ最小値を上回る。
- Boundsが元Assetから許容範囲以上に逸脱していない。
- 元表面との距離が原則Voxel 1～2個以内に収まる。
- 体積変化率、面数、連結成分数がRecipeの期待範囲内である。
- 薄すぎる部位、退化三角形、自己交差が残っていない。

技術的に閉じていても、自己交差が残る、入口や空洞を誤って埋めた可能性がある、Projectionが内部面へ誤吸着した可能性がある場合は`Success`にせず`NeedsReview`とする。レポートには修復したLoop、Solidify厚、Closing半径、Projection採用／拒否頂点数と移動距離分布、自己交差数、修復前後の体積・Bounds・面数と断面プレビューを保存する。

### 10.6 BlenderテンプレートとPythonの分担

公開可能な空の`.blend`テンプレートにGeometry Nodes、入力Collection、封鎖Collection、出力Collection、検証用設定を保持できる。Pythonはファイル入出力、Recipe適用、パラメータ設定、処理実行、検証、終了コードを担当する。これにより、失敗AssetだけをGUIで開いて中間状態を確認できる。

Voxel RemeshではUVや元の頂点属性を保持する必要はない。Solid Cut Meshは内部判定と断面輪郭のための形状であり、Display Meshは別途保持する。断面はUVやトライプラナー質感へ依存せず、Unity側の共通トゥーンシェーダーへ粘土色グレーまたはデバッグBase Colorを渡して描画する。

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

変換コード、汎用Recipe Schema、ライセンスAssetを含まないテンプレート、検証コード、Blender版Manifest、Bootstrapは公開する。Blender本体、Syntyの入力Asset、`.unitypackage`、付属`.meta`、生成されたSolid Cut Mesh、Physics Proxy、加工済み断面素材は公開しない。`/Tools/Blender/`と`/Generated/`をgitignoreし、公開履歴への混入をCIで検査する。

Synty POLYGON City Packの購入原本は、公開Unityリポジトリと分離した非公開Git LFSリポジトリ`C:\Users\%USERNAME%\src\zantetsuken-assets-private`で管理する。2026-08-26時点で、`Vendor\Synty\POLYGON_City\v5\Original`へ`POLYGON_City_SourceFiles_v5.zip`と`POLYGON_City_Unity_2022_3_v1_12_4.unitypackage`を格納済みであり、両ファイルはLFS対象である。ダウンロード元と格納先のSHA-256一致を確認済みとする。

非公開リポジトリへのアクセスはSyntyライセンス上の許可を持つ開発チームだけに限定する。購入原本は変更せず保存し、展開したFBX／Texture、Phase 0.2のEarly Licensed Fixture／Asset対応表、加工Asset、Solid Cut Mesh、Physics Proxyなどのライセンス派生物も公開Git履歴へ入れない。公開リポジトリから参照する場合も、公開Submodule、公開Release、公開CI Artifact、共有Cacheを経由してAsset本体を配布しない。

公開CIはPlaceholder Assetで前処理と切断ロジックを検証する。Syntyを用いる変換と製品ビルドは、許可されたローカル環境または限定private runnerだけで実行し、公開Artifactと共有Cacheへ生成物を残さない。

## 11. モーション方針

モーションは原則として既製HumanoidクリップをUnityでリターゲットする。NPCはIdle、Walk、Run、Turn、Startled、Run Awayを初期最小セットとし、頭・胸の視線や腕IKをプロシージャルに重ねる。切断時は現在姿勢を固定して物理へ移行するため、切断方向ごとの専用死亡モーションは作らない。

- NPC：MixamoまたはQuaternius Universal Animation Library系の既製モーションを候補とする。

- プレイヤー：刀と手はVRコントローラーの実測姿勢を使用し、必要なら腕だけTwo Bone IKで補間する。

- 全身アバターは初期段階で必須にせず、手袋と刀だけでも体験検証を可能にする。

- 再生速度、位相、左右反転、視線対象を変え、少数クリップから群衆の多様性を作る。

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
| D-009 | モーション | 既製Humanoidモーションをリターゲットし、IKで補正 | 確定 |
| D-010 | データ表現 | Display Mesh／基底Solid Cut Mesh／実行時Cut Shell／Physics Proxyを分離 | 確定 |
| D-011 | 対象環境 | 初期製品スコープをPCVRとし、Quest単体対応は当面除外 | 確定 |
| D-012 | 性能目標 | 実アプリの両眼描画90fpsを基準とし、再投影を常用前提にしない | 確定 |
| D-013 | 開発順序 | 非VR PoCと性能評価を先行し、早期XR確認後にVR操作・UIを導入 | 確定 |
| D-014 | 検証HMD | Quest 3Sを有線Quest Linkで初期PCVR検証に使用 | 確定 |
| D-015 | 攻撃演出 | 三日月形の斬撃波を扇状に有限速度で飛翔させ、接触時に分離 | 確定 |
| D-016 | 先行計算 | 到達猶予で未来姿勢、表示Mesh、Convex切断を投機評価 | 確定 |
| D-017 | 未来評価 | 未来イベントDAG、信頼度、世代検証、Commitから成る評価器を実装 | 確定 |
| D-018 | 物理予測 | 必要時に局所PhysicsSceneを固定刻みで先読みし、接触時に検証 | 技術検証付き確定 |
| D-019 | 文書管理 | 本Markdownを唯一の設計正本とし、DOCXは使用しない | 確定 |
| D-020 | 観測基盤 | 固定名ProfilerMarker、Flow Event、固定長TraceLogger、Editorタイムライン、異常時保存をPoC開始時から実装 | 確定 |
| D-021 | ログ方針 | 状態遷移をenumと整数IDで記録し、高頻度の文字列生成とDebug.Log連打を避ける | 確定 |
| D-022 | Asset前処理 | 固定バージョンのBlenderをヘッドレス実行し、Python＋テンプレートで一括変換 | 確定 |
| D-023 | Solid生成 | 開口封鎖、Voxel Remesh、簡略化、watertight検証で充填Solid Cut Meshを生成 | 確定 |
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
| D-045 | 遠距離モブ | Far／Dormantモブはキネマティックな経路・Animation位相・粗い時空間予約を先行確定し、切断計算の猶予へ利用する | 技術検証付き確定 |
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
| D-061 | OpenXR Capture責務 | Windows PCVRのD3D11（D-064）だけから開始し、Projection Swapchain ImageをRelease前に専用GPU TextureへCopyしてTraceと同期する | 技術検証付き確定 |
| D-062 | 映像の証拠範囲 | Projection Captureはアプリ提出画像の証拠とし、Meta compositor、Reprojection、レンズ補正、Quest Link圧縮後の最終HMD像は保証しない | 確定 |
| D-063 | Capture相関 | Unity FrameId、OpenXR Frame連番、predictedDisplayTime、Pose、TestRunId、Slash／Object／Task ID、Commit経路を共通Capture Recordで関連付ける | 確定 |
| D-064 | 開発Capture Profile | Windows PCVR、D3D11のみ、SDR／sRGB、MSAAなし、Dynamic Resolutionなし、Single Pass Instanced、App Projection Layer 1枚、左眼45fpsを初期固定構成とする | 確定 |
| D-065 | Capture Fail Fast | 実行時のGraphics API、Format、Sample Count、Array Size、Layer、SubImageが固定Profileと違う場合は録画だけを停止し、構成差をTraceする | 確定 |
| D-066 | Capture環境記録 | Unity／Package／Meta Runtime／Quest OS／GPU／Driver／Swapchain／Link設定をRun Manifestへ保存し、環境差のあるRunを同一条件として比較しない | 確定 |
| D-067 | cooking非同期化 | Collider Bake／cookingを視覚切断のクリティカルパスから外し、Active境界は完了前でも命中フレームから断面と相対移動による隙間を表示する。Dormant境界は単独では即時表示を要求しないが、HasDetached／Cull失効済みOperationでは実装簡略化用の補助Capとして描画され得る。この場合もDormant側の相対移動と切断演出は起動しない | 確定 |
| D-068 | Pending物理共有 | `PendingPhysicsSplit`中は左右の表示破片を1つのFragmentGroup、Rigidbody、旧Colliderへ追従させ、小幅のめり込みと隙間内の旧Colliderを一時許容する | 確定 |
| D-069 | 物理分裂Commit | Bake済みConvexの完成後、物理ステップ境界で左右Rigidbodyへ分裂し、親の線速度・角速度から各重心位置の速度を継承する | 確定 |
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
| D-080 | Stencil互換Group | 全World Cut Plane、Side／半空間、Offset、Cap描画状態が一致する対象は、画面上で重なってもWinding Countの和集合として同じStencil Colorへ統合する | 技術検証付き確定 |
| D-081 | 両眼Cap可視性Cull | 論理破片×切断面ごとに左右眼Facingを判定し、全Capが両眼とも裏向きの互換Groupは彩色前にStencil Clear／Volume／Cap処理から除外する | 技術検証付き確定 |
| D-082 | Stencil競合領域 | Front／Back相殺後の非ゼロ領域を可視Cap Boundsで保守的に包み、Raw Stencil書き込みの一時的な重なりは競合としない。各眼でOBB投影または可視Cap Boundsのどちらかが非交差なら同一Colorを許可する | 技術検証付き確定 |
| D-083 | バックグラウンド実行基盤 | CPU幾何・予測計算はC# Taskの大量発行ではなくJob System＋Burstを基本とし、Task／AwaitableはI/Oと非同期制御へ限定する。Unity Objectの適用とGeneration Commitはメインスレッドで行う | 確定 |
| D-084 | Convex Job Pipeline | Physics ProxyのConvex分割、検証、質量特性、MeshData出力と`Physics.BakeMesh`をJob化し、Mesh公開とCollider／Rigidbody Commitだけをメインスレッド／物理ステップ境界に残す | 技術検証付き確定 |
| D-085 | Native Cook比較Probe | Unity Built-in 3D Physicsの`Physics.BakeMesh`を製品経路の正本とし、Native PhysXの頂点Hull経路、完全Topology経路、直接生成経路を早期に測定専用Probeで比較する | 確定 |
| D-086 | Native採用Gate | Cook時間の倍率差だけでは置換せず、Unity経路が実際のP99／90Hz要件を破り、Unity側最適化で解消せず、Native統合Prototypeまで成立した場合だけ物理経路の部分置換を再検討する | 確定 |
| D-087 | Voxel後Surface Projection | Voxel／SDFをTopology修復用中間表現とし、簡略化前にTrusted Exteriorだけへ距離・法線・包含制約付きで投影する。Projection失敗部はVoxel位置へ戻し、UV／Material転送は必須としない | 技術検証付き確定 |
| D-088 | Solidの自己交差契約 | Topological Watertightと自己交差のないGeometrically Valid Solidを区別する。自己交差は即時clip／Stencilで条件付き表示できても、Stable Solid Cut Mesh、反復切断、Physics Proxyの合格入力にはしない | 確定 |
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
| D-112 | 早期実Asset Fixture | Phase 0.2でSynty多数モデルへ共通の簡易Blender処理を適用し、Render／Solid／Convex Gateを自動通過した少数だけを非公開LicensedRepresentative Datasetへ固定する。個別修理と最終最適化は行わず、投入母数とReject理由を保持し、全Asset互換性の証拠にはしない | 確定 |
| D-113 | 早期Triangle Variant | Phase 0.2のRender／Solid FixtureはOriginalと100／500／1,000／2,000／5,000 Triangle Targetを共通Decimate Presetで生成する。Source／Voxel基底がTargetを上回れば削減率に関係なく生成し、Target以下のNoOpと同一hash Aliasだけを重複Geometryから除外する。SolidはTargetごとに再検証し、Convex削減系列とは分離する | 確定 |
| D-114 | 早期Voxel Variant | Voxel64／128／256をTopology再構成系列としてDirect Decimateと分離し、SourceとのTriangle差や増減にかかわらず基底Variantを保持する。限定Post-Decimate行列だけを生成し、各結果を再検証して大偏差はBenchmarkOnlyとする | 確定 |
| D-115 | 早期Fixture canonical契約 | 数値Gate、カテゴリ、Triangle帯、決定論的／資源上限をEarlyFixtureSelectionProfileへ固定し、Import前の投入母集団をEarlyFixtureSourceCatalogへ固定する。Source／Script／Presetはcanonical file index bytesでhashし、Blender実行前とReceipt確定前に実treeとの完全一致を再検証する。VariantIdはSource＋Tier内で一意、DatasetCaseIdはTierを含める。Selection ReportはLaunch／Bootstrap／Importを区別した完全決定表に従うStatus／Attempt列と変動時間を記録する。採用GeometryはZantetsuCanonicalGeometry v1へ正規化し、binary32 decode後にRender／Solid／Convex Gateを再実行する。LicensedRepresentativeDatasetIndexは再検証合格GeometryのFormat／Version／相対path／byte長／canonical file hashを完全なfile許可リストとしてTool／Profile hashとともに確定する。Index canonical bytesのSHA-256をBenchmark DatasetContentSha256とし、Report／Index両hashをLicensedFixtureSelectionReceiptで監査可能に固定する | 確定 |
| D-116 | Solid自己交差Broad Phase | 最大20万Triangleに対する全pair列挙を禁止し、epsilon拡張AABBの決定論的`SolidCandidateBvhV1`で候補を生成してcanonical pair順へsort／deduplicateする。200万一意候補をProfile上限とし、次のpairで`ProfileUnsupported／CandidatePairLimit`へ決定論的に停止する。上限内の実時間／メモリ超過だけをResource retryへ流す | 確定 |

## 13. 未決事項

| ID | 論点 | 選択／質問 | 影響 | 決定時期 |
| --- | --- | --- | --- | --- |
| O-001 | 初期ターゲット | 解決済み：PCVRを採用（D-011） | Quest単体は当面スコープ外 | 2026-08-21 |
| O-002 | 目標FPS | 解決済み：両眼描画90fpsを基準（D-012） | 再投影は安全網として扱う | 2026-08-21 |
| O-003 | Temporary Renderer上限 | 同一物体の`TemporaryRenderCapRecordSet`について、補助Dormantを含む実Cap 2、3、4枚のどれを標準上限とするか | 描画コストと連続斬り感 | T-003後 |
| O-004 | 断面表現 | 共通トゥーン＋粘土色グレーは確定。機械内部や人体で追加記号・部品表現を使う範囲 | 年齢区分とアート制作 | アート検証時 |
| O-005 | 切断可能範囲 | 建物・道路まで切断対象に含めるか | レベル設計とメモリ | 垂直スライス後 |
| O-006 | 破片寿命 | 最大動的破片数、消去時間、スリープ規則 | 物理CPUと視覚密度 | T-010後 |
| O-007 | Collider仮状態 | 旧Collider維持時間とプレイヤー手・身体／周辺破片の例外判定。刀は論理Sweepのみ | 違和感と実装複雑度 | T-005後 |
| O-008 | NPC構成 | Synty人物をそのまま使う範囲と顔・体型改造量 | 独自性と制作工数 | アート検証時 |
| O-009 | データ保存 | 切断状態をセーブ対象とするか | 再現性・容量・ロード時間 | ゲームループ決定時 |
| O-010 | ネットワーク | 将来的なマルチプレイ要否 | 切断イベント同期設計 | 企画判断 |
| O-011 | Trace保存量 | リングバッファ秒数、最大イベント数、書き出し形式の最終値 | メモリ、調査可能時間、ツール工数 | T-020後 |
| O-012 | Voxel品質 | Asset分類別のVoxel Size、Adaptivity、穴封鎖閾値 | 輪郭精度、面数、処理時間 | T-022後 |
| O-013 | 建物分割 | 建物チャンクの標準寸法と意味境界の指定方法 | 局所切断性能とアート破綻 | T-024後 |
| O-014 | 自動修復閾値 | 自動封鎖径、平面誤差、Solidify厚、Voxel Closing半径 | 誤封鎖、輪郭誤差、処理成功率 | T-027～T-029後 |
| O-015 | Blender更新方針 | 4.5.12 LTSから次版へ更新する判断基準と更新頻度 | API互換性、生成差分、保守期間 | LTS更新候補発生時 |
| O-016 | Unity CLI再評価 | 実験的CLIとUnity PipelineをCIへ採用するか | 保守性、自動導入、外部依存 | CI構築時 |
| O-017 | Slash Latch閾値 | 刀先速度、移動量、Sample Window、方向分散、再発射間隔 | 誤発射、体感遅延、面安定性 | T-034後 |
| O-018 | SlashFront分解能 | 頂点追加の角度／時間／距離閾値、最大頂点数、辺分割・簡略化規則 | 当たり精度、VFX連続性、CPU負荷 | T-035～T-036後 |
| O-019 | Edge Gate閾値 | Edge Lead Score、CutSample速度・位置、Recovery解除、異常速度上限 | 復路誤発射、取りこぼし、連続斬り感 | T-038～T-041後 |
| O-020 | Grip校正 | 左右持ちの既定Offsetとユーザー校正を提供するか | 刀表示の一致、刃方向判定、導入工数 | XR操作検証時 |
| O-021 | AI LOD境界 | Near／Mid／Far／Dormantを分ける最短介入時間、距離、更新周期 | CPU予算、見た目、予測再利用率 | T-045後 |
| O-022 | MobPlan Horizon | Tier別の先行確定時間とCommittedUntilの長さ | 切断計算猶予、無効化率、メモリ | T-044～T-046後 |
| O-023 | モブ予約 | 粗い時空間予約のセル寸法、競合解決、群衆密度上限 | 交差回避、自然さ、計画費用 | T-047後 |
| O-024 | Unity更新頻度 | 6000.3.22f1から同一LTSパッチへ更新する条件と回帰基準 | 修正取込み、再インポート時間、安定性 | 更新候補発生時 |
| O-025 | 前縁逆行閾値 | 無視する逆行距離／角度／継続時間、Span bin数、自己交差epsilon | 手ぶれ耐性、U字誤前縁、斬撃の途切れ感 | T-052～T-053後 |
| O-026 | Unity録画設定 | 左眼45fpsはD-064で確定。縮小率、リング秒数、異常後保存時間、静止画枚数 | GPU負荷、保存量、調査可能性 | T-054後 |
| O-027 | API Layer対象 | 解決済み：Graphics APIはD3D11のみ（D-064）。Encoderは開発PCで利用可能なNVENC／AMF／QSVから1系統を選ぶ | 実装工数、GPU同期、対応PC | Encoder確認時 |
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
| T-016 | 未来評価器 | 締切順評価、未Schedule Work Itemの取消、Schedule済みJobの世代不一致破棄が競合なく動く | 遅延、進路変更、再切断で評価順を意図的に反転 |
| T-017 | 局所物理予測 | 介入なしでは高率に再利用でき、予測費用が利益を下回る | 姿勢誤差、採用率、予測CPU時間を測定 |
| T-018 | Animation未来姿勢 | 既知Clipで接触姿勢を十分な精度で生成できる | 予測骨姿勢と実接触姿勢を比較 |
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
| T-044 | MobPlan再現性 | 同じ入力、Seed、NavMesh、PlanGenerationから同じ計画区間を生成できる | 固定シーンの計画Hash、経路、Animation位相、予約を比較 |
| T-045 | AI LOD予算 | 遠距離モブ数を増やしても計画CPUとメモリが予算内に収まり、近距離反応を阻害しない | Tier別人数、更新周期、Horizonを変えてProfilerとTraceを比較 |
| T-046 | MobPlan無効化 | プレイヤー介入、経路遮断、別切断で旧計画と依存切断成果物がCommitされない | PlanGenerationを意図的に更新し、Task破棄とFallbackを自動照合 |
| T-047 | 時空間予約 | Farモブ同士が粗い予約下で目立って重ならず、予約計算が局所的に完了する | 密度別に競合数、再計画数、CPU時間、見た目を測定 |
| T-048 | モブ先行切断 | 遠距離モブの計画済み姿勢が、命中前のMesh／Convex完了率を改善する | 距離、Tier、Horizon別にCommit率、破棄率、Pending時間を比較 |
| T-049 | Mob Trace完全性 | MobPlan生成から利用、無効化、再計画、切断Commitまで因果を追跡できる | MobId、PlanGeneration、SlashId、TaskIdで保存Traceを自動照合 |
| T-050 | 断面表示一貫性 | 仮断面から実断面、Stableグレーへの移行で陰影や輪郭が目立って変化しない | 共通トゥーン設定下で箱、凹形、人物を多方向に切断し、両眼映像とフレーム差分を確認 |
| T-051 | 断面デバッグ表示 | 赤／青／緑等が実際の処理経路と一致し、色覚補助表示を含めても90fps予算を阻害しない | 各Commit／Reject／Pending経路を強制し、Traceとの一致、GPU時間、Draw、選択パネル更新負荷を測定 |
| T-052 | U字折返し | U字・往復軌道で同一SlashFrontが前後二重にならず、往路は維持され復路だけがFinalized後の別Slash候補になる | 逆行量、速度、角度、停止時間を変えた入力Traceで頂点順序、Finalized理由、命中分布を検査 |
| T-053 | 前縁一価性 | Extendingと飛翔の全時刻でSpan binごとの前進位置が1つ以下となり、非隣接辺交差と頂点順序反転がない | ランダム軌道と極端な手首運動を再生し、各更新後に不変条件を自動検査 |
| T-054 | Unity選択的録画 | 片眼映像、異常前後リング、限定静止画がFrameId／Traceと一致し、録画有効時も性能予算内 | 解像度、30／45fps、Encoder、リング長別にGPU／CPU時間、Dropped Frame、保存遅延を比較 |
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
| T-067 | Stencil相殺・互換Group | 整合したCut Shellの閉部分ではFront／Backがゼロへ相殺され、非ゼロ領域が可視Cap Bounds内に収まる。キャップ互換な重複対象は同一Colorで正しく和集合表示され、不一致は確実に別Groupとなる | 同一Slashの静止／共通親／別Rigidbody、追加Cut、異Material、Debug色差、偶数重なり、多重Countに加え、面向き不正、非閉形状、Near Plane、非対称clip／Depth、MSAA境界を作り、残留Stencil範囲、Key分類、World Plane epsilon、画像差、Fallback、Color削減率を検査 |
| T-068 | 両眼Cap可視性Cull | 両眼とも裏向きの互換Groupだけが安全に早期除外され、片眼可視、面近傍、正負破片でCap欠落や点滅を起こさずStencil仕事を削減する | 左右眼でFacingが一致／不一致となる配置、面横断、頭部微動、正負Cap、Frustum外を再生し、Cull判定、ヒステリシス、Stencil Draw／GPU時間、左右眼画像差を比較 |
| T-069 | Convex Job Pipeline | Convex分割と複数`Physics.BakeMesh`がメインスレッドを停止させず、世代不一致成果物を適用せず、Pending物理共有から安全に分裂できる | 破片数、面数、同時Slash数、Fast Cook／Fast Simulationを変え、各Job段階時間、Schedule数、Worker占有、Main Thread Commit時間、Bake P50／P95／P99、Generation Reject、物理差し替え時Impulseを測定。同一Mesh同時Bakeを不変条件として検出する |
| T-070 | Unity／Native Cook Probe | U1／N1／N2／N3を同一入力と近似条件で再現測定し、Unity経路の実費用、Hull再計算の寄与、完全Topology／直接生成の改善上限を工程別に説明できる。製品Geometry完成前の早期Probeであり、T-076の前提ではない | 8～255頂点級、単発／Batch、Fast Cook／Fast SimulationをRelease相当で反復し、P50／P95／P99、Throughput、各工程時間、Thread占有、メモリ、失敗率、出力形状、接触／Query品質を測る。Target×Stage×ExecutionMode許可規則に従い、単一DatasetCaseIdと固定規模軸を持つ各系列のManifest／Resultを作り、Suite Indexでhashと件数を固定する。N1のHullComputation、N1／N2のPhysXFormatBuild／StreamSerialize／StreamLoad、N3のDirectInsertionを独立系列として復元でき、版違いと非利用可能なNative生成物を明記する |
| T-071 | Surface Projectionと自己交差 | Voxel形状より主要Silhouette／曲面誤差を改善しつつ、Projection／Reduction後も自己交差、面反転、退化、境界、体積異常を残さず、実Mesh切断の単純Loop前提を満たす | 車、建物、家具、薄板、近接二重面、内部装飾を含むDatasetでProjectionなし／無制約Shrinkwrap／制約付きProjectionを比較し、距離分布、Silhouette、Normal、包含、最小厚み、自己交差、投影拒否率、Triangle数、前処理時間、多方向切断Loop次数と三角形化成功率を測定する |
| T-072 | 固定物体の即時切断 | cook遅延中も固定側が動かず、自由と証明された側だけが仮分離し、Commit後も位置・速度・Constraintが連続する | 単一Anchor、両側Anchor、面近傍Anchor、Compound Graph、連続切断、先行評価Reject、cook遅延／失敗を再生し、分類時間、誤Impulse、固定点変位、自由側軌道、Traceを検査する |
| T-073 | Dormant Cut再可視化 | LogicalCutOperationをIncomplete／FullyFixed／HasDetachedへ一意に集約し、失効していないFullyFixedだけは子数にかかわらず即時Stencil／仮Cap／分離を起動しない。HasDetachedまたはCull失効済みではFixed同士の補助Dormant Capを含む全非Suppressed Cap、Incompleteでは既知Active Capだけを描く。交差する後続切断ではCull失効後にDetached部品とその全境界断面が同一フレームに現れる | 大型建物を縦1面、交差2面、3面で切り、2子全Fixed、凹形状の3子全Fixed、3子中2子Fixed＋1子Detached、Anchored／Detached／Unknown混在、切断済み親への後続Cut Operationを検査する。default Incomplete、三値優先順位、ActiveTemporaryBoundarySetとTemporaryRenderCapRecordSetの差、補助Dormantを含む実描画件数と2～4枚上限を確認する。過去FullyFixed操作の直接子を再切断し、Cull失効が境界Active化より先に同一フレームで起き、一度失効したCullが再有効化されないことを検査する。Cap pair／Coverage探索、Cap Buffer圧縮、Mesh部分更新を行わず、線状亀裂、局所Z-fighting、禁止する面状Z-fighting、Cap欠落、旧面復活、背景Job完了順、再切断世代も確認する |
| T-074 | 支持Topologyモデル | 同一物体にActive／Dormant／Suppressed境界とPending／Ready Geometryが混在しても状態を損なわず、境界決定表、FragmentGroup物理集約、LogicalCutOperation三値集約、Cull失効、全履歴面の再評価、物理状態遷移、世代Rejectが決定論的に動作する | 正負Supportの全9組み合わせに加え、`Anchored／Detached`のActive境界と`Unknown／Anchored`のSuppressed境界が同一Groupへ混在するFixtureを再生する。OperationSupportStateのdefault Incompleteと`Incomplete > HasDetached > FullyFixed`、子数2／3以上、Cull失効済みFullyFixedを検査する。PendingSupportClassification中に旧Rigidbody、Collider、Constraint、TransformとGroup運動が変わらず、既知Active境界だけがActiveTemporaryBoundarySetへ入り、Suppressed境界とDormant補助CapはTemporaryRenderCapRecordSetへ入らないことを確認する。HasDetached／Cull失効済みではDormantが自発的なExposure要求を持たないまま補助Capとして実描画集合へ入ることも検査する。子数0／1／65、境界数0／257、重複子ID／境界ID、親と同じ子ID、未知ID、自己境界、境界へ接続しない子、世代不一致を原子的にRejectし、部分的なOperation／Fragment／Boundary公開がないことを確認する。後続切断では祖先OperationのCull失効、過去境界Active化、三値再集約の順序、不可逆失効、再分類後の集約遷移、Timeout Fallbackに加え、Operation作成、全Child／Boundary／正負Endpoint Link、ParentObjectGeneration、SupportGraphGeneration、状態遷移、Cull失効とReject理由を固定Traceから復元する。3子以上で同じID集合から異なる接続Graphを作るFixture、Generationの0／`uint.MaxValue`、Endpoint欠落／重複／反転、作成Trace束の中断、件数不一致、完了マーカー欠落を検査し、不完全Traceを状態再現の合格根拠にしない純粋C#テストを行う |
| T-075 | Render／Convex対応 | Pending／Represented／Missing／Shared／AmbiguousとNone／Keeper／DebrisCandidate／PreserveFallbackを固定値どおり決定論的に扱い、物理表現不能な小Fragmentだけをデブリ化して、大型・重要・未分類・曖昧なFragmentを誤消去しない | default初期化、全Status／Role組合せ、1 Render対1 Convex、1 Render対複数専有Convex、対応なし、複数Render対1 Convex、多対多、専有＋Shared混在、複数大型共有、閾値近傍、世代不一致を合成する。不正組合せReject、近似被覆、Keeper選択、未分裂Fallback、SharedGroupLocalIdの0予約・世代内一意性・非再利用、Trace Reasonと対応／Shared連結成分の復元を検査する |
| T-076 | Geometry／Cook Microbenchmark | 製品の表示Mesh切断、Convex切断、T-077検証済みTemporary Low-Poly Proxy生成、`Physics.BakeMesh`を工程別に再現測定し、単発レイテンシとJob定常処理容量を分離して、入力規模からP95／P99完了時間を見積もれる。T-070の早期Probeを製品入力分布から補完・再解釈する | 公開合成DatasetをRelease Player相当／Burst有効でWarm-up後に反復する。計算KernelのSingle-Thread µs/op、Bake／Commitの直列単発Latency、Job Batchのcuts／triangles／convexes／cooks per second、Schedule／Complete latency、Worker占有、Main Thread Commit、GC／Nativeメモリ、失敗率を規模別に保存する。Target×Stage×ExecutionMode許可規則、Metric／Unit組合せ、系列一意性を検査し、`ColliderCommit + SingleThreadKernel`と`PlaneClassification + MainThreadCommit`をRejectする。ManifestのDatasetCaseIdと全規模軸をResultへjoinし、Samples／P50／P95／P99と容量式の説明変数を一意に復元する。同一Suiteへ同じDatasetId・異なるDatasetContentSha256を持つLatency／Throughput等を混在させたFixtureをjoin前にSuite Rejectし、別Suiteまたは別DatasetIdなら受理する。Bytes／Count Samples `[1,2]`からMean `1.5`を取得順binary64左畳みで再計算し、101件以上のPercent系列でもCountを範囲違反にしない。対象処理の失敗／FallbackがRejectedではなくFailureRateへ入ること、一部計測不能時の件数、全試行計測不能時のSuite Rejectを検査する。Manifest／Result相互ID・hash・件数、Aggregate再計算、Result差し替え、欠落／余分Entry、開始／終了clean検証、途中HEAD変更、Repository外一時出力、Index-last原子的確定、未知Schema／property Rejectを試験する。Manifest 64 KiB、Result 64 MiB／100万Sample、Index 64 MiB／10万Entryと呼び出し側のより小さい上限、宣言件数超過、非seek入力、過剰nesting、末尾dataを配列確保前にRejectし、全Loaderに無制限APIが存在しないことを確認する |
| T-077 | Temporary Low-Poly Proxy正しさ | 実装した各品質段階が有限で決定的なGeometryを生成し、表示ProxyはBounds／切断側／Triangle上限、物理Proxyはwatertight／面向き／凸性またはCompound規約／PhysX上限を満たす。不正入力を成功扱いせず安全な下位Fallbackへ移す | 中央／端／非交差、薄形状、極端なAspect、複数Fragment、退化Bounds、NaN入力を合成し、同一入力Hashからの出力一致、有限頂点、退化面、Bounds逸脱、切断側分類、体積、凸性、Primitive重複、上限、Validation Reason、Fallback順を検査する。合格した品質段階だけをT-076へ渡す |
| T-078 | 早期Licensed Fixture選抜 | Asset別Recipeや手修正なしの共通Presetで多数のSyntyモデルを処理し、Render／Solid／Convex Fixtureを決定論的に選抜できる。失敗を無理に通さず、合格少数の実Asset試験をPhase 0.25／1／3／4へ供給し、公開Repoへライセンス派生物を漏らさない | 家具、車、建物、道路設備、小物とProfile固定の全Source Triangle帯から多数を投入し、固定Blender／Script／Presetで2回実行する。Resource状態を除くTier合否、Geometry hash、形状統計、Reject Stage／Reasonの完全一致を要求する。各AttemptのProcessMilliseconds／Peak Working Set／Tool結果は保存するが時間／Peakの完全一致を要求せず、初回Timeout／MemoryLimitを固定順Attemptsから復元し、ResourceLimitExceededを単一Processで1回再試行して再超過をResourceDeferredとしてGeometry Rejectと分離する。開放Render、容易なwatertight Solid、単純Convex、自己交差、複雑開口、Profile上限、時間／メモリ超過を含める。公開合成FixtureとLicensedRepresentativeの結果・保存先を分離し、投入総数とカテゴリ／Triangle帯／Tier別合格率を残して選抜集合を全Asset互換性と誤認しないこと、公開Git／Artifact／CacheへAsset名対応表とGeometryが混入しないことを検査する |
| T-079 | Early Fixture Reduction Variant | Original／Tri100／Tri500／Tri1000／Tri2000／Tri5000を同じSource Fixtureから決定論的に生成し、実Triangle数ごとの切断性能と形状検証結果を比較できる | 元Triangle数が50、100、120、500、900、1,100、2,200、5,500以上のAssetを含める。Target以下のNoOp、SourceがTargetを1 Triangleだけ上回る生成、異Target同一hash Alias、TargetごとのRender／Solid合否、Solidだけの自己交差／watertight失敗、BenchmarkOnly分類を検査する。同じSourceのRender／Solid Originalが`fixture_017.render.original`／`fixture_017.solid.original`として衝突せず、VariantIdはTier内一意、Tier間Aliasは禁止されることを確認する。NoOp／AliasがDatasetCaseを作らず同TierのCanonicalVariantIdを持つこと、ReportのSource／Target／Actual／Ratio／AppliedとDatasetCaseId対応、Manifest InputTriangleCountがActualと一致しOutputTriangleCountを切断後出力に維持すること、Convex削減設定へTriangle Targetが漏れないことを確認する |
| T-080 | Early Fixture Voxel Variant | Voxel Remesh基底をTriangle削減率だけで省略せず、相対解像度と限定Post-DecimateのTopology／Solid化／性能差を再現測定できる | 同一SourceをVoxel64／128／256へ通し、SourceよりTriangleが減る、同数、1 Triangleだけ変わる、増える各caseを含めて全基底を保持する。Voxel基底とSourceの同一hash Alias、Bounds Scale変更時の相対Voxel Size一致、限定行列外Variant非生成、Voxel基底がPost Targetを1 Triangleだけ上回る生成を検査する。各Variantのwatertight、自己交差、体積変化、Bounds差、sampled表面距離、BenchmarkOnly、DatasetCaseId、Report項目、Manifest InputTriangleCount、決定論的Profile上限とResource状態を確認する |
| T-081 | Early Fixture canonical schema | Profile／Source Catalog／Bundle Index／Report／Dataset Index／Receiptから投入母集団、選抜条件、カテゴリ／Triangle帯、全試行、採用Geometry集合と各fileを一意に復元でき、変動時間がDatasetContentSha256を変えない | Profile property順／数値境界／Triangle帯境界、Catalog全Source被覆、Blender Launch／Bootstrap／FBX Import失敗時の正しいStage、null Triangle／Band、決定的失敗Entry、全Status／Reason、AssetCategory、SourceTriangleBand再計算、nullable規則、Entry sort、Tier付きDatasetCaseId、Tier内VariantId一意性、Tier間Alias禁止、最大2件Attemptsと初回Timeout／Memory／Tool結果、Resource retry、未知propertyを検査する。Status完全決定表の全許可列に加え、`Selected + ToolFailed`、1 AttemptのResourceDeferred、GeometryRejected末尾Resource、ToolFailed後retry、Resource中間ReportのIndex／Receipt確定をRejectする。Bundle Kind、NFCと`/`のpath正規化、ordinal順、case-fold衝突、`.`／`..`、symlink／junction、空directory無視、raw file hash、Catalog予約EntryとSource参照照合、archive再圧縮によるhash不変を試験する。既存Bundle Indexを残したままroot fileを変更／追加／削除したcase、開始後からReceipt前の変更、長さ／hash不一致を3 BundleすべてでRejectする。Geometry rootではIndex記載pathとの完全一致、欠落／余分file、重複／case-fold衝突、path traversal、symlink、byte長、raw hash、Index外metadataを試験する。ZCGではFormat／Version／Kind、reserved／長さ／末尾data、非有限／負の0、position／triangle／face／hull入力順のPermutation、cyclic rotation、winding、重複／退化、decode後再serialize一致を試験する。本文の非対称三角形を固定golden bytes／SHA-256へ照合し、Root／Object translation、unit scale、Y/Z swap、負determinant transformを個別に変えたcaseで変換順とwindingを確認する。TriangleMeshはbinary32後の全体Boundsから求めたtwiceAreaがepsAreaちょうど、1 ULP下、1 ULP上のFixtureをBlender EncoderとUnity Decoderで同じ合否にする。Solidはbinary32化／position weldによってBoundary、Non-Manifold、向き、自己交差、連結成分、volumeが変わるFixtureをZCG後GateでGeometryRejected／CanonicalGeometryとし、Index／Receiptへ入れず、Report統計がdecode後値になることを確認する。`SolidSignedVolumeV1`は成分Bounds中心からの同一形状を原点付近と大きく平行移動したcase、およびbinary64で`V`が`epsVolume`ちょうど、1 ULP下、1 ULP上となるcaseを用い、canonical Triangle／成分順、termごとの除算、左畳みを共有Validatorで照合する。`ClosedTriangleDistanceV1`はproper crossing、特に一方のedgeが他方のface内部を貫通する一方で頂点はface上になくedge-edge接触もないcase、coplanar overlap、非共有vertex／edge／face接触、epsilonちょうど、1 ULP内側／外側のnear miss、正規の共有edge／vertex、重複Triangle、AABB broad phase境界を検査する。共有vertex／edgeだけで接するcase、および接触集合が共有simplexから正確に`epsDistanceSquared`境界まで達する十分な高さの正常隣接Triangleは許可する。そこから`epsOutsideSquared=nextUp(epsDistanceSquared)`へ1 ULP外れた残余接触、または同じ共有indexを持ちながら近傍外でcoplanar overlap、proper crossing、near missを持つcaseは`SharedSimplexResidualV1`でSelfIntersectionにする。包含不能は保守的Rejectとし、Blender HarnessとUnity Editor Harnessが同じScript BundleのValidator artifactから同じ件数／Rejectを得ること、未知algorithmやValidator version不一致をFallbackせずRejectすることを確認する。`SolidCandidateBvhV1`は最大20万Triangleが空間的に分離したFixtureで全pair走査せず0または局所候補だけを生成し、入力Triangle列挙順やworker数を変えても同じBVH split、sort済みpair列、Candidate Countを得ることを確認する。候補数がProfile上限ちょうどのcaseを処理し、次の一意pairで`ProfileUnsupported／ProfileGuard／CandidatePairLimit`とsentinel `MaxSolidCandidatePairCount + 1`へ決定論的に停止すること、checked counter／node／byte overflow、AABBがepsilonちょうど接するpair、重複候補のsort／deduplicate、上限内の実Timeout／MemoryだけがResource retryへ入ることを試験する。Convexはbinary32量子化の前後、Face平面距離／半空間距離／signed volumeがepsilonちょうど、1 ULP内側、1 ULP外側となるFixture、非平面Face、内向きFace、開放edgeをBlender EncoderとUnity Decoderの双方で同じ合否にし、同一形状の列挙順やFBX metadataだけを変えてもGeometryContentSha256が一致することを確認する。Profile 64 KiB、Catalog／Bundle各16 MiB／10万Entry、Report 64 MiB／10万Entry／20万Attempt、Index 64 MiB／10万Variant、Receipt 64 KiB、ZCG 64 MiBと必須のより小さい呼び出し側上限を、配列確保前、非seek入力のlimit／limit+1 byte、宣言件数不一致、過剰nesting、末尾dataで試験する。同じGeometry／Tool hashでAttempt時間だけを変えた2 Reportは同じDataset Index hashを参照するが異なるReport hash／Receiptになること、ReportまたはIndex差し替え、Receipt欠落をRejectすること、Geometry、Profile、Source Package、Blender、Script、Presetのいずれかを変えるとIndex hashが変わることを確認する。Index hashをGeometryBenchmarkRunManifest.DatasetContentSha256へ設定してSuite Loaderまで照合する |
| T-082 | Capture Draft／Publication Recovery | 現行Record中心のライブCaptureをDraft中心へ置換し、最終Manifest確定前にもrequest、readback、PNG encodeを相関できる。freeze後はStaged Draftだけを原子的に最終Recordへ昇格し、Trace先行公開とCapture再試行を一意に復元できる | Factory／Registry／Submission／Scheduler／readback completionをDraftで通し、Drop Reason 0～9の固定値、既存1～4互換、各経路との一意対応、unknown Reject、lease予約失敗、Registry満杯、readback／encode失敗、PNG staging失敗、取消、freeze drain Timeoutのrollbackと`Pending -> Dropped`終端化を検査する。`受付停止 -> producer稼働中のbounded drain -> producer取消／join／静止 -> Terminal Intent Queue最終完全drain -> Queue／私有Buffer所有権照合 -> 残存Pending強制Drop -> 通常Trace producer静止 -> 通常FIFO完全Drain -> terminal列構築／専用Append -> Recorder Freeze -> Snapshot -> Summary`を各境界で停止させる。drain中とjoin直前の成功Stage／通常Drop Intentが最終drainで必ず処理され、完成済みPNGを理由9へ誤分類しないこと、drain中のEncoded／通常Drop EventとBarrier前残存Eventが通常領域だけへ入り、最大強制Drop＋RingFrozenが専用reserveへall-or-noneで入り、通常領域満杯でも`AwaitingFreezeTerminal`から早期Frozenしないことを確認する。terminal EventType／TestRunId／ID順／末尾Ring／件数、通常Queue非空時Append、直接APIの状態違反、PostRoll／reserve境界、通常領域overflow時Incomplete、reserve不足Profileを検査する。DroppedにPNGがなくてもFinalizerが成功し、StagedのPNG欠落、DroppedへのPNG混入、Pending残存、TestRunId／Context不一致、重複ID、件数不一致では最終Recordを1件も公開しない。Plan Schema v1のRunInitializationIdを含むproperty順／型／null禁止／最短integer／NFC、16 MiB／10万Entry／path／呼出側上限、非seek `limit + 1`を検査する。信頼base rootから`runs/run-{TestRunId}`を導出し、OS排他lockの同時取得拒否とprocess crash解放、staging作成直後／各init tmp・Rename／final作成／各ready確定でのcrashを再現する。片側root、空／tmp-only root、ready片側、完了後staging削除済みfinal-onlyを正しく復旧し、marker／InitializationId／Root hash／Peer hash不一致、同一／祖先base root、別Run再利用を隔離する。許可marker／tmp集合、rooted／UNC／drive／`.`／`..`／空segment／backslash／case-fold衝突／symlink／junction／reparse point／TOCTOU差し替えをRejectし、固定path導出とRun root内解決を要求する。staging file flush、Plan-last commit marker、Trace公開前durabilityを検査する。Trace公開前の各失敗点でFrozen入力とdurable stagingを保持し、Summary payload変更時はManifest hashではなくtrace／bundle index hashだけが変わることを確認する。Trace公開後はManifest hashを変えず、PNGだけ／sidecarだけ公開後のクラッシュから一致側を保持して欠落側だけを再試行し、内容衝突だけをhard errorとして上書きしない。`capture.index.tmp`書込中／flush後／rename前、Index確定直後／通知前／cleanup途中の各クラッシュを再現し、tmpを完了証拠にせず、Planと同一なら再利用、partialなら削除再生成、canonicalな所有不一致なら隔離する。全期待PNG／sidecar成功後に同じcanonical bytesの`capture.index`をPlan削除前にdurable確定し、CaptureCompleteと期待集合を復元する。後日のArtifact削除／改変検出、pre-trace orphan隔離、明示放棄時のTraceOnlyCaptureIncomplete、bounded staging枯渇時のbackpressureも検査する |

T-082では追加で、`MaxInFlightDraftCount`が受付済み全Pending Draftをqueue横断で厳密に制限し、Registry外Pendingが存在しないことを検査する。freeze時のimmutableな`ForcedDropFrameIdSet`に対して、terminal列の欠落、余分、重複、順序違反、Reason違反、TestRunId違反、Ring欠落／複数を個別に与え、すべてall-or-noneでRejectされcapture列が不変かつ`AwaitingFreezeTerminal`に留まることを確認する。Buffer構築失敗、検証失敗、reserve書込み失敗の各地点から同じ集合で再試行して初回成功時だけFrozenとなり、成功前はSnapshot／Summary／Manifest／Plan／Exportが不可能で、明示Abortではbundleが公開されないことを確認する。Run root所有権はstaging／finalの2 lock pathを決定順に取得するものとし、異なるstaging base＋同じfinal base、同じstaging base＋異なるfinal base、2本目取得失敗、逆順要求、process crashを試験する。途中失敗では先に得たhandleが解放され、両Run rootが未変更であり、再試行可能であることを確認する。

T-082ではさらに、通常領域に空きがある状態と満杯の状態の双方で`BeginFreezeTerminalAppend`だけが`CapturingPostRoll -> AwaitingFreezeTerminal`を起こし、producer稼働中、通常Queue非空、drain未照合、およびBegin再呼出しをRejectして状態とcapture列を変えないことを検査する。terminal reserve有効時のpublic `Freeze()`が直接Frozenへ進めないこと、Legacy reserve 0だけが旧契約を維持することも固定テストに含める。`MaxInFlightDraftCount`と`MaxDraftCountPerRun`の境界、終端Entryを保持したままPending Slotを再利用する長時間Run、総Entry 100,000件、100,001件目の受付拒否を試験する。Pending不足／総Entry不足の`CaptureFrameAdmissionRejected`はID 0とKind／Value1の固定割当を持ち、Draft／Dropped／Plan件数へ入らないこと、理由5を`RecordDropped`へ渡すとRejectされることを確認する。

T-082ではLogger Seal境界をproducer enter前／active中／退出直後／Sealing後／最終Drain後へ移動し、active writer数のincrement後に行う`Open`再確認の成功をenqueue成功の線形化点とする。この線形化点が`Open -> Sealing` CASより前のEventだけがQueueと通常領域へ入り、active writer数のincrementがCASより前でも`Open`再確認がCASより後ならQueueへ入らず、Sealing中の拒否としてcutoff前ならRun Failure Count、cutoff後／Sealed後ならPost-Seal診断Countだけを増やすことを検査する。raw ParallelWriterをCapture Runから取得できないこと、Main Thread EnqueueとBurst writerが同じgateを通ること、late enqueueがあってもBegin／Appendが停止しないことを確認する。forced drop 0件／1件／上限件で各terminal Eventの22 fieldを1 fieldずつ改変し、Draft Trace Context、Checkpoint、未使用0、状態、Reason、Value、負の0／非有限の不一致がall-or-noneでRejectされることを試験する。既存2引数constructorがreserve 0、Capture Factoryがchecked `MaxInFlightDraftCount + 1`を設定すること、internal constructorの負値／超過／overflow、reserve有効時public `Freeze()`のfalse・無変更、Legacy時の既存bool挙動を固定テストへ含める。

T-082ではFailure Count cutoff直前／同時／直後にSealable writerを競合させ、各拒否がSealed Run CountまたはPost-Seal診断Countの厳密に一方へ入り、Sealed Countと生成済みSummaryが以後変化しないことを検査する。Summary取得後に保持済みwriter copyから試行してもQueue、Sealed Count、bundleのStateが変わらず、Post-Seal Countだけが増えることを確認する。通常Draft Dropの理由6～8と強制Dropの理由9へ同じ非ゼロSlashId、FrontEdgeId、ObjectId、ObjectGeneration、TaskIdを持つ既存CaptureFrameTraceContextを与え、いずれも12相関fieldが一致し、`FromState=Pending(0)`、`ToState=Dropped(2)`、元ContextにないSlashGeneration／Mob／Planだけが0であることを全22 field Validatorで確認する。通常Draft Dropについては、単一Terminal CoordinatorへDrop対Drop、Drop対Stageを同時投入し、確定した先頭Intentだけが共有資源のrollbackまたはStaging採用、Registry終端遷移、Pending Slot解放を各1回実行し、敗者が勝者の資源へ触れないことを検査する。Dropped確定直後にLogger破棄、seal競合、Queue／Native書込み失敗を個別に注入し、Trace enqueueが失敗してもDraftがDroppedのまま、freeze時のForcedDropFrameIdSetへ入らず理由9へ再分類されないことを確認する。失敗した通常Drop Traceはcutoff前のRun Failure Countを増やしてRunをIncompleteにし、RegistryのDrop Trace発行状態は成功・失敗とも`Attempted`へ一度だけ進む。同じCaptureFrameIdで消費APIを再呼出ししてもEvent、Failure Count、Draft状態が増減しないことを検査する。Legacy `RecordDropped`の理由1～4は既存の`FromState=0`／`ToState=0`を維持し、新設`RecordDraftDropped`が理由6～8だけを受理すること、理由9を両通常APIへ渡すとRejectされterminal Builderだけが生成できることも固定テストへ含める。既存CaptureFrameProfileの7引数constructorと2引数`CreatePhaseZeroUnityLeftEye`の結果が不変でTrace容量を持たず、PhaseZeroCaptureProfileSetが4096／32／10000を返し、Profile ID不一致、Trace Profile境界、Factory構築を決定論的にReject／受理することを試験する。

T-082ではTerminal Intent Queue容量を`checked(2 * MaxInFlightDraftCount)`の直前／一致／1件超過で検査し、同一Draftの未処理Intent上限2件と同一DraftについてRun中に受理される総数上限2件、3件目の拒否、Queue全体満杯、Coordinator drainとの競合を試験する。`TerminalIntentEnqueueStatus`の全固定値について、`Accepted`だけが私有Buffer所有権をCoordinatorへ移し、`Backpressured`だけがproducer所有のまま再試行可能、`DraftAlreadyTerminal`／`IntentLimitExceeded`／`RunNotAccepting`はproducerが私有Bufferを解放して再試行しないこと、`InvalidIntent`は所有権を移さずRunをFail Fastすることを検査する。Queue満杯後はdrainでAcceptedへ進む一方、受理総数2件到達後の3件目は何度待ってもBackpressuredへ変化せず、無限再試行しないことを固定する。複数条件が同時成立する場合のstatus優先順も1件ずつ試験する。freeze取消時はBackpressured Intentを再試行して受理させるか、`RunNotAccepting`を受けてproducer自身が私有Bufferを解放し所有数0をacknowledgeするまでjoin成功とみなさない。join直前の最後のenqueue、join後Queue非空、最終drain途中を個別に停止し、最終drain後だけQueue件数0、受理数と処理数一致、Queue所有Buffer数0、producer保持Buffer数0となること、その後の残存Pendingだけが理由9になることを確認する。

## 15. 実装ロードマップ

| 段階 | 焦点 | 主要成果物 | 完了条件 |
| --- | --- | --- | --- |
| Phase 0 | 非VR基盤・観測 | Unity 6.3 LTS 6000.3.22f1、Universal 3D／URP、Repo・ignore・Package Lock、固定テスト、Editor更新手順、入力抽象化、WorldPhysicsProfile、ProfilerMarker、Flow、TraceLogger、最小タイムライン、FrameId同期のUnity選択的キャプチャ、CaptureFrameDraft／CaptureDraftRunContext／Factory／Registry、Draft状態／Drop tombstone、append-only Drop Reason、Freeze Barrier／通常領域とterminal専用reserve／AwaitingFreezeTerminal、Draft対応Submission／Scheduler／readback completion、OS lock／二相Run root marker、Run専用Durable PNG Staging Store、CaptureFrameDraftFinalizer、canonical CapturePublicationPlan／path-safe bounded Loader、永続Capture Index／tmp Recovery、FrozenRunPublicationCoordinator、Summary付きExport Snapshot、Trace／Capture二段階公開と再試行Recovery、T-019／T-020／T-082 | 固定Editor版から非VRで再現可能な性能基準、重力Profile、Work Item／Job時系列、対応画像を取得する。ライブCaptureは最終Manifestを要求せずDraftとPNG stagingまで進む。受付停止後にin-flightをdrainしてproducerを静止し、通常FIFOを通常領域へ完全Drainしてから、強制Drop／RingFrozenだけを専用reserveへ直接追記してRecorderをFrozenにする。freeze時にPendingを残さずterminal TraceをFrozen列へ含める。Stagedだけを既存CaptureFrameRecordへ原子的に昇格し、Droppedは期待集合から除く。TestRunIdでRun rootを導出し、OS lockと相互binding markerで排他的に初期化／Recoveryして、PlanとstagingをTraceより先にdurable確定する。Trace bundle公開前失敗では同じFrozen Runを再構築し、公開後の一部Artifact失敗では最終Manifestを変えず、片側公開も含め欠落fileだけ再試行する。全期待CaptureFrameIdのPNG／sidecar検証後に永続`capture.index`を確定して初めてCaptureCompleteとなり、一時worktreeで更新・復帰手順も確認する |
| Phase 0.2 | 早期Licensed Fixture選抜 | 固定版Portable Blender最小Bootstrap、Source FBX列挙、共通簡易Preset、`EarlyFixtureSelectionProfile`、`EarlyFixtureSourceCatalog`、Source／Script／Preset `CanonicalBundleIndex`と完全tree Verifier、Launch／Bootstrap／Import Stage、Render／Solid／Convex Gate、Original／Tri100／Tri500／Tri1000／Tri2000／Tri5000、Voxel64／128／256と限定Post-Decimate、ZantetsuCanonicalGeometry v1 Encoder／Decoder／Numeric Kernel／ZCG後Gate、`SolidSignedVolumeV1`、Script Bundleへhash固定した共有`SolidGeometryValidatorV1`／`ClosedTriangleDistanceV1`、`EarlyFixtureSelectionReport`、`LicensedRepresentativeDatasetIndex`、`LicensedFixtureSelectionReceipt`、非公開Geometry Dataset、T-078／T-079／T-080／T-081 | Import前に投入Source／カテゴリ／file hashをCatalogへ固定し、Blender起動・Bootstrap・Import失敗を正しいReport Stageから復元できる。多数のSyntyモデルを手修正・Asset別Recipeなしで一括処理し、Profile固定カテゴリ／Triangle帯を含む少数のRender／Solid／Convex Fixtureを再現選抜できる。小プロップの自然な低Triangle Originalと、大きいAssetのDirect Reduction Variant、Topologyを再構成するVoxel Variantを区別して有効実入力を用意する。基底がTargetを上回れば削減率にかかわらず生成し、Target以下のNoOpと同一hash AliasだけをDataset Geometryから除外する。数値Profileで全Gateを判定し、Status完全決定表に合う全AttemptをReportへ保持してResource状態をGeometry Rejectと分離する。採用候補をZCG v1へ決定的serialize／decodeし、binary32後のTriangle退化とRender／Solid／Convex Gateを再実行して、失敗VariantをCanonicalGeometry Rejectへ移す。Solid volumeは成分Bounds中心とcanonical順で一意に再現し、自己交差はBlender／Unity別実装ではなく同一共有Validator artifactの完全決定表で判定する。Blender実行前とReceipt直前に3 Bundle rootをIndexと完全照合し、Index canonical bytesからDatasetContentSha256を再現し、ReceiptでReport／Index両hashを固定してからBenchmarkへ渡す。入力・派生GeometryとAsset対応表を非公開Repoだけへ保存し、選抜結果を全Asset互換率と扱わない |
| Phase 0.25 | Cook比較Probe | 公開合成Convex Dataset、Phase 0.2の非公開LicensedRepresentative Convex補助Dataset、U1 Unity BakeMesh Harness、N1／N2／N3 Native PhysX Harness、工程別Timer、Repository外のManifest／Result／Suite Index Bundle、結果レポート | 製品Geometry完成前の早期Probeとして、同一入力でUnity経路の実費用とNative改善上限をP50／P95／P99まで再現測定でき、N1／N2／N3の必須Stage差、版・設定差、Manifestと実測Resultのhash対応を記録できる。合成Datasetをcanonical正本とし、LicensedRepresentativeは実Asset傾向の補助確認に限定する。T-076の前提とはせず、Native PhysXを製品Runtime依存にはしない |
| Phase 0.5 | XRスモークテスト | OpenXR、Quest 3S有線Link、Grip Pose、Tracking State、GripToKatanaOffset、Single Pass | 空シーンで両眼90Hzと左右の刀姿勢・追跡復帰を確認 |
| Phase 1 | 即時切断 | `NoFixedSupport`と明示されたテスト対象、公開合成MeshとPhase 0.2の非公開Render Fixture、共通切断入力、単一clip、分離オフセット、簡易断面、ヒット演出、事前Shard済み専用テストMeshによるVertex Pulling／Indirect Batch描画性能PoC、VFX Graph汎用Fallback | 非VR入力で、固定支持を持たないと明示した箱と選抜済みSynty代表プロップに即時の隙間を表示する。支持属性が不明な対象や地面・壁・基礎へ固定された対象は切断対象へ入れない。任意切断由来の微小Fragment判定やclip＋ポリゴン崩壊は行わず、全Fragmentを通常の塊としてclip表示する。事前Shard済み専用Meshだけを通常数千Triangle・少数Drawで描画し、GPU経路の性能とFallbackを確認する |
| Phase 1.5 | 固定支持Topology | `FixedSupportAnchor`、Node／Edge、`LogicalFragment`、`LogicalCutOperation`、`CutBoundaryRecord`、Support／Exposure／Geometry／Work Result状態軸、三値`OperationSupportState`、`FullyFixedCullInvalidated`、`PendingSupportClassification`、Support→Exposure決定表、全LogicalFragment→FragmentGroup物理状態集約、LogicalCutOperation構築Validator、Anchor到達性、Anchor／SupportGraph世代、Commit検証、純粋C#単体テスト、Operation作成／Link／状態遷移／Cull失効／Rejectの支持Trace契約 | 手書き／合成FixtureでT-074を満たし、Collider切断やcookなしで境界ごとのDormant／Active／Suppressed分類、操作ごとのIncomplete／FullyFixed／HasDetached集約、後続切断時のCull先行失効、複数境界混在時のGroup物理状態、分類不能時の物理完全維持と既知Active境界の描画、補助Dormant Cap、再分類遷移、全履歴面の再評価、世代不一致／不正Operationの原子的Reject、保守的Fallbackと固定TraceからのOperation復元を決定論的に再現できる。完了後に固定支持対象を切断対象へ追加する |
| Phase 2 | 仮断面・影強化 | Cut Shell、ゼロKerf、Dormant Cut再有効化、`LogicalCutOperation`、三値`OperationSupportState`、`FullyFixedCullInvalidated`、`ActiveTemporaryBoundarySet`／`TemporaryRenderCapRecordSet`、Fully Fixed Cut Operation Cull、実Fragment Mesh早期公開、Ready中の表示継続と原子的Geometry Commit、OBB交差Cap Bounds Polygon、両眼Frustum／Facing Cull、Front／Back相殺とResidual Stencil Support検証、CapCompatibilityKey／互換Group、可視Cap Bounds競合判定、Winding Count Stencil、左右眼Stencil Conflict Graph／Greedy Coloring、Color単位Volume／Cap Batch、共通トゥーンの粘土色グレー、処理経路デバッグ色、ShadowCaster用per-instance clip／Offset、Stable片面／Pending両面Batch、XR両眼対応、Pending Cut／Stable履歴管理 | 2～4連続切断と複数対象の画面重複でStencilが混入せず、意味上のActive境界集合と実際の描画Cap集合を分離できる。補助Dormant Capを描画コストと実Cap 2～4枚上限へ数え、Ready到達だけでは表示を戻さず、実Mesh適用とCommitted遷移が同じ描画フレーム境界で成功した後だけ対応Recordを外す。失効していないFullyFixedは子数にかかわらず大断面の即時Stencil仕事を発生させず、HasDetachedまたは失効済みでは全非Suppressed Cap、Incompleteでは既知Active Capだけを描く。後続切断では祖先OperationのCullを境界Active化より先に不可逆失効させる。Cap pair／Coverage探索、Cap単位Buffer compaction、Mesh部分更新を行わず、Geometry Commit後もCutBoundaryRecord、LogicalCutOperation、Cull失効履歴、支持履歴を残す。許容する線状亀裂／局所Z-fightingと禁止する面状Z-fightingを区別し、Detached化した瞬間に過去断面を欠落なく再表示する。OBBが重なってもCap非交差なら安全にBatchされ、互換Groupは統合され、両眼不可視またはCull EligibleなFullyFixed操作は欠落や点滅なく除外される。相殺不能入力はFallbackし、Shadow MapではStencil Capなしの影近似が許容範囲に収まる |
| Phase 3 | 表示ジオメトリ | Job＋Burst三角形切断、Count／Write Job、ReadOnly／Writable MeshData、断面生成、RenderFragment接続成分、Triangle数／面積／体積／重要度Metadata、後続Debris Corner Stream生成用出力、メインスレッドMesh公開、世代Commit、Phase 0.2 Solid Fixture回帰 | 仮表示から実Meshへ無停止で置換し、重い頂点処理がMain Threadへ戻らない。公開合成Fixtureに加えて選抜済みSynty Solid FixtureでもCap／Fragment生成を確認する。任意切断由来Fragmentは物理Convex対応が確定するまで塊として表示され、Phase 3だけでは大きさを理由にデブリ化せず、clip中の表面Triangle崩壊を起こさない |
| Phase 4 | 物理 | 全体0.5G仮設定、FragmentGroup、PendingPhysicsSplit／PendingSupportClassification／PendingAnchoredSplit、全LogicalFragmentの物理状態集約、Phase 1.5支持モデルとの接続、分類不能時の旧物理完全維持とTimeout Fallback、Active境界描画とGroup運動の分離、固定側Impulse禁止、自由側解析仮運動、Native Convex B-rep、Count／Write／Validation Job、RenderFragment／LogicalConvexFragment対応グラフ、近似被覆、Represented／Missing／Shared／Ambiguous、SharedResolutionRole、cook前デブリ判定、Temporary Low-Poly Proxy生成Kernel／Validation／Fallback、Runtime Debris Geometry Arenaと後追いGPU崩壊、Job化`Physics.BakeMesh`、Fast Cook初回分裂、選択的Fast Simulation再Bake、別Mesh差し替え、Upgrade Scheduler、質量特性、速度継承、Generation Reject、Timeout品質低下、保守的な仮予算管理、Phase 0.2 Convex Fixture回帰、T-063／T-070／T-075／T-077との差分再確認 | cook遅延中も既知Active境界の即時表示を維持し、分類不能時は旧物理とGroup運動を変えない。1 Render対複数専有Convexを正常にRepresentedとし、物理表現不能な小Fragmentだけをデブリ化する。大型・重要・Ambiguous、明確なKeeperのないSharedは共有またはProxy再構築Fallbackへ残す。Temporary Proxyの実装済み品質段階がT-077を通り、不正入力は下位Fallbackへ移る。T-076前はSchedule数、Worker占有、Batch、同時Bake、Nativeメモリへ保守的な仮上限を設定し、Arena不足でも待機・再確保しない。分類後は固定側を動かさず自由側だけを安全に分離する。公開合成Fixtureと選抜済みSynty Convex Fixtureの両方で、Convex分割／BakeがMain Threadを停止させず、二段階Colliderを安全に昇格する。Unity経路が要件を満たす限り維持し、満たさない場合だけD-086のGateを評価する |
| Phase 4.1 | Geometry／Cook性能Baseline | 固定合成Dataset、Phase 0.2 LicensedRepresentative補助Dataset、Single-Thread Kernel Harness、Job Batch Harness、表示Mesh／Convex／T-077検証済みTemporary Proxy／Bake工程Timer、Repository外のManifest／Result／Suite Index Bundle、P95／P99容量式 | Phase 3／4の正しい製品実装をT-076に従い、公開合成Datasetをcanonical正本、選抜済みSynty Fixtureを別の非公開補助Suiteとして測定する。各DatasetCaseIdの固定規模軸とSamplesをjoinしてKernel単発µs、Bake／Commit単発Latency、定常Throughput、Job End-to-End latencyを再現する。Suite内DatasetId→DatasetContentSha256一意性、Target×Stage×ExecutionMode、FailureRate／Rejected契約、bounded Manifest／Result／Index Loaderを検証し、Phase 4の保守的仮上限を校正する。O-035／O-039の初期確定予算と斬撃波Deadlineまでに処理可能な対象数を根拠付きで決め、T-070の早期結果を再解釈できる |
| Phase 4.5 | 飛翔斬撃と未来評価 | Gesture状態機械、Edge Direction Gate、Recovery、NonCutting素通り、Slash Latch、Span／Travel Axis、単調・一価SlashFront、逆行／自己交差Finalized、前縁VFX、帯状Sweep、Candidate Flight Bounds、評価DAG、先行切断、Commit検証 | 復路とU字軌道で二重前縁や誤斬撃を作らず、Latch直後から三日月前縁が飛翔・命中し、Extending中も前縁が成長しながら進み、遠距離対象の多くが接触時に完成Meshへ即移行 |
| Phase 4.6 | 予測拡張 | 局所PhysicsScene、未来Animation姿勢、信頼度別フォールバック | 動的対象でも予測採用率と予測費用が基準を満たす |
| Phase 4.7 | モブ未来計画 | Mob Future Planner、MobPlan／PlanGeneration、AI LOD、経路・Animation先行確定、時空間予約、Trace | 介入なしの遠距離モブで計画再利用率と先行切断完了率が基準を満たし、介入時は安全に無効化される |
| Phase 4.8 | OpenXR Projection Capture | Windows API Layer、D3D11固定、SDR、MSAAなし、Dynamic Resolutionなし、Single Pass、Projection 1枚、左眼45fps、Release前GPU Copy、固定Profile検証、GPU Encode、Capture Record／Run Manifest同期 | 切断PoCの異常をProjection画像とTraceで再現調査でき、想定外構成はFail Fastし、非録画時との差が性能予算内。不要なら導入を見送れる |
| Phase 5 | 人形 | 姿勢スナップショット、CPUスキニング、骨proxy分類、物理移行 | 基本動作中のNPCを任意方向に切断 |
| Phase 5.5 | Asset自動前処理 | Phase 0.2の選抜Report／失敗例を入力に、完全なPortable Blender Manifest／Bootstrap、固定版ヘッドレス実行、Asset別Recipe、開放Mesh修復、Voxel／SDF内部充填、Trusted Exterior分類、制約付きSurface Projection、Projection後自己交差検証、見た目を保つReduction、UV／Material再構成、Micro Attachment連結成分抽出／Recipe分類、AttachmentId／Anchor／対象Triangle／ShardId生成、実Asset用FixedSupportGraph生成、Solid／Proxy／Debris Atlas生成、検証、キャッシュを実装する | Phase 0.2でRejectした複雑Assetも対象に含め、古いシステム版と共存しながら代表家具・車・建物を別PCでもGUIなしで再現生成する。主要外形をVoxel結果より改善し、自己交差入力をStable Solidへ通さず、重要部品を除外しながら微小付属物を安定分類する。事前分類済みMicro Attachmentだけは命中同フレームにAliveMask消去とGPU崩壊へ移行できる。Phase 1.5の合成Fixtureを実Asset由来Graphへ置き換えて同じ契約テストを通し、Phase 0.2より広いAsset範囲と製品品質を達成する |
| Phase 6 | コンテンツ | Synty City街区、10プロップ、シェーダ統一、既製モーション | 垂直スライスとして一連の遊びが成立 |
| Phase 7 | 最適化 | 端末別品質、破片LOD、ジョブ優先度、遠距離確定、ストレス試験 | ターゲット実機で性能予算を満たす |

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

- 表示Mesh、Convex、T-077検証済みTemporary Low-Poly Proxy、cookの固定DatasetベンチマークがRelease／Burst環境で再現でき、Single-Thread µs/op、Job定常Throughput、End-to-End P95／P99からWorker予算、同時切断数、Batch Size、同時Bake数を説明できる。単一DatasetCaseIdの規模軸、工程別Stage、許可されたExecutionMode、Manifest／Result hash、Samples／Aggregate件数をSuite Indexから検証でき、同じManifestへのResult差し替えを拒否する。同一Suiteでは各DatasetIdが厳密に1つのDatasetContentSha256へ対応し、異なるhashの系列を容量式へ混在させない。Manifest／Result／Index Loaderはそれぞれ64 KiB、64 MiB／100万Sample、64 MiB／10万Entryのschema上限と呼び出し側のより小さい上限を配列確保前に強制する。対象処理の失敗をFailureRateへ残し、計測不能な試行だけをRejectedとする。既存TraceRunManifest／bundleのCodecとGolden Hashは変化しない。

- Unity `Physics.BakeMesh`とNative PhysX比較Probeの入力、版、設定、工程別結果が再現可能に保存され、倍率差だけを理由にNative Backendが製品へ混入しない。Native再検討時はD-086のGateを満たした証拠を残す。

- 処理中に再切断しても、古いジョブ結果で形状が巻き戻らない。

- NPCを移動中に切断し、姿勢固定から剛体破片への移行が成立する。

- 代表的な連続切断シナリオで目標フレームレートとメモリ予算を満たす。

- Phase 0.2ではImport前のSource CatalogとSource／Script／Preset Bundle Indexから投入母集団、匿名ID／カテゴリ対応、全入力file、Script、Presetを再現識別でき、Blender Launch／Bootstrap／Import失敗も正しいStageで欠落させない。多数のSyntyモデルを共通簡易Presetへ投入し、個別修理なしでRender／Solid／Convex Fixtureを少数選抜できる。Render／SolidはOriginal、100／500／1,000／2,000／5,000 Triangle級のDirect Variant、Voxel64／128／256基底と限定Post-Decimateを持ち、小プロップの自然な低Triangle Original、強制Reduction、Topology再構成を区別する。基底がTargetを上回れば削減率にかかわらず生成し、NoOp／Aliasは新しいDatasetCaseを作らない。採用形状はFormat／Version固定のZCG canonical bytesへ変換し、入力列挙順や非本質的metadataからGeometry hashを分離する。数値Profileにより形状合否を再現し、Resource状態と変動時間を形状Gate／Dataset hashから分離する。投入母数、Profile固定のAsset Category／Source Triangle Band、Process Mode、Source／Target／Actual／Ratio／Applied、Voxel Size、形状偏差、Tier別合格数、全Attempt、Reject Stage／Reason、出力hashがReportへ記録され、IndexからDatasetContentSha256を再現できる。ReportとIndexの両hashをReceiptで固定し、Receiptが欠落・不一致のDatasetをBenchmarkへ渡さない。選抜済み少数の成功を全Asset互換率として扱わず、LicensedRepresentative GeometryとAsset対応表は非公開Repoだけに存在する。

- 10種類のアセットが、Blenderヘッドレス処理によってDisplay／Solid Cut Mesh／Physics Proxyの自動またはRecipe駆動工程を通過する。

- 生成AssetがTopological Watertightだけでなく、自己交差、面反転、退化のないGeometrically Valid Solid検証に合格し、同一入力・Recipe・Blender版から再現可能に生成される。

- 制約付きSurface ProjectionがVoxel由来の大形状誤差を改善し、誤吸着または自己交差を生じる頂点はVoxel位置へFallbackする。採用／拒否理由とReduction前後の誤差がレポートに残る。

- 小さな欠損、底面欠落、片面シェル、微小隙間を自動修復でき、意味が曖昧な大開口は`NeedsReview`として停止する。

- 切断帯へ触れたMicro Attachmentは即時表示と確定Meshの双方から不可逆に消え、差し替えや古い非同期結果で復活せず、極小Rigidbodyを生成しない。

- Micro Attachment消去時は元部品の実GeometryをShard ClusterとしてGPUだけで飛散・ディザ消滅させ、通常500～3,000 Active Triangleを少数Drawで処理する。連続発生でもGameObject、Collider、Rigidbody、GCを増加させず、左右眼で同じ消滅模様に見え、予算超過時だけ汎用破片または即時消去へ低下する。

- 自動修復前後のBounds、体積、表面偏差が記録され、許容値を外れた生成物を採用しない。

- Synty入力と派生したSolid Cut Mesh／Physics Proxyが公開Git履歴、公開CI Artifact、公開キャッシュへ含まれない。

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

- 遠距離モブの計画済み軌道とAnimation位相を切断先行計算へ利用でき、プレイヤー介入時は旧`PlanGeneration`の成果物が適用されない。

- Unity Editor更新時にプロジェクトを作り直さず、専用ブランチで固定テストとXRスモークテストを実行し、不合格なら旧固定版へ復帰できる。

- 不変条件違反時に直前30秒を目安とするTraceが保存され、Editorで再読込して原因系列を調査できる。

- PoCでは選択的な片眼映像または静止画をFrameIdからTraceへ対応付けられ、録画停止時と比較して90fps性能判断を歪めない。

- OpenXR API Layerを有効にした検証では、D3D11固定Capture Profile上でProjection画像と`predictedDisplayTime`、Pose、TestRunId、Slash／Object／Task IDを一意に関連付け、API Layer自身のGPU／CPU負荷も別計測できる。Profile逸脱時はゲームを止めず録画だけをFail Fastし、Run Manifestへ理由と実構成を残す。

## 17. Codexでの継続更新ルール

- 決定が変わった場合は既存行を消さず、状態を『廃止』にして代替決定IDを記録する。

- 未決事項は結論、根拠、決定日を追記して決定事項へ移す。

- 技術検証は測定環境、再現手順、数値結果、スクリーンショット／Profiler参照を残す。

- ロードマップのPhase完了条件を満たす前に次Phaseへ進む場合は、既知の負債として記録する。

- 新しい機能提案は『即時応答』『幾何精度』『物理整合』『性能予算』のどれへ影響するかを明記する。

- DOCXを再生成せず、このMarkdownのみを正本として更新する。

> **次の推奨アクション** Phase 0として非VR固定テストと共通切断入力に加え、ProfilerMarker、Flow Event、固定長TraceLogger、最小Editorタイムライン、FrameId付きの選択的静止画／片眼録画を先に用意する。まず公開合成箱で性能基準、完全なWork Item／Job時系列、対応画像を取得する。次にPhase 0.2で固定版Blenderの最小実行経路、共通簡易Preset、`EarlyFixtureSelectionProfile`、Source Catalog、3種のCanonical Bundle Index、ZCG v1 Encoder／Decoder、Report／Dataset Index／Receipt Codecを作り、多数のSyntyモデルからRender／Solid／Convex Fixtureを自動選抜する。Render／SolidにはOriginal、100／500／1,000／2,000／5,000 Triangle Direct Target、Voxel64／128／256基底と限定Post-Decimateを生成・再検証して非公開Datasetへ固定する。続いてPhase 0.25のCook比較Probeを合成Convex正本とReceipt検証済みIndex hashで識別したLicensedRepresentative補助Datasetで実施し、Native依存を製品へ持ち込まず結果を固定する。その後Phase 0.5でQuest 3S有線Linkの両眼表示と90Hzを確認する。OpenXR API Layerは切断PoC成立とT-054完了後まで実装しない。

## 18. 用語

| 用語 | 定義 |
| --- | --- |
| Stable Geometry | バックグラウンド生成が完了し、表示へ確定適用された実Fragment Mesh／Cut Shell。ColliderやRigidbodyのCommit完了は含意しない |
| Pending Cut | 実命中により登録済みだが、`CutBoundaryRecord.GeometryState`がまだ`Committed`ではない切断。ExposureStateによりActive／Dormant／Suppressedのいずれでもよく、Collider完成度は含意しない |
| ActiveTemporaryBoundarySet | `ExposureState == Active`かつ`GeometryState != Committed`の意味上のActive境界集合。Incomplete操作で描画可能な既知境界の基準にはするが、補助Dormant Capを含む実描画コストや枚数上限の正本ではない |
| TemporaryRenderCapRecordSet | 当該フレームに実際のStencil／Cap Batchへ投入するCap Record集合。FullyFixed Cull Eligibleなら空、HasDetachedまたはCull失効済みなら全非Suppressed未Commit Cap、IncompleteならActiveTemporaryBoundarySet対応Capから成る。補助Dormant Capも描画コストと実Cap 2～4枚上限へ数え、Geometry Commit成功後だけ対応Recordを外す |
| Solid Cut Mesh | Blenderプリプロセスで入力Assetから生成する、表示には使わないTopological Watertightかつ自己交差のないGeometrically Validな基底形状。初回の内部判定、断面生成、反復切断の入力となる |
| Cut Shell | 基底Solid Cut Meshまたは直前のStable Cut Shellへ確定済み切断を適用して派生する、現在のObjectGenerationを表す閉じた実行時形状。Stencil内部判定と次回切断に使う |
| Physics Proxy | 物理接触と高速切断のための低複雑度Convex／Compound |
| FragmentGroup | 物理分裂Commitまで、複数の表示・論理破片を1つのRigidbodyと旧Colliderで支持する一時的な物理単位。物理状態は全LogicalFragmentのSupportStateを集約して一意に決める |
| PendingPhysicsSplit | 見た目と論理状態は切断済みだが、左右のBake済みColliderが未完成でFragmentGroupの物理モデルを共有している状態 |
| FixedSupportAnchor | 地面、壁、基礎、固定Constraintなど、切断後も動かしてはいけない支持位置を表す点または小領域。Micro AttachmentのAnchorとは別概念 |
| FixedSupportGraph | Compound Convex／構造チャンクの接続とFixedSupportAnchorをプリプロセスで記録し、切断後に固定Anchorから到達可能な成分を判定する軽量グラフ |
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
| FullyFixedCullEligible | `OperationSupportState == FullyFixed && !FullyFixedCullInvalidated`から導出する値。trueの場合だけLogicalCutOperation全体のTemporaryRenderCapRecordSetを空にできる |
| Suppressed Cut Boundary | 支持分類未完了、世代不一致、接続曖昧などにより安全な露出状態を決定できない`CutBoundaryRecord`。clip、Stencil、仮Cap、Offset、Impulseを起動せず、再分類後にDormantまたはActiveへ遷移する |
| Kerf | 切断によって除去される物理的な幅。本作では0とし、見える隙間は破片の相対移動だけで生じる |
| Cooking Profile | `Physics.BakeMesh`と`MeshCollider`へ同一指定するcookingOptionsの構成。初回分裂用Fast Cookと選択的Upgrade用Fast Simulationを使い分ける |
| Physics Upgrade | Stable Fast Cook破片と同じ形状の別MeshをFast Simulationで再Bakeし、安全な物理ステップ境界でColliderを昇格させる処理 |
| Micro Attachment | Physics Proxyで表現しない微小な付属部品。切断帯へ触れた場合は物理破片を作らず不可逆に全体消去する |
| Attachment AliveMask | AttachmentIdごとの生存状態。即時表示、確定Mesh、再切断、世代管理で共有し、消去済み部品の再出現を防ぐ |
| GPU Micro Debris | 事前分類済みMicro Attachment、または物理Convex対応がMissing／SharedのDebrisCandidateで補助的な消去条件も満たしたRuntime Fragmentの実GeometryをShard Cluster化し、Vertex Pulling、解析運動、Indirect Batch、Opaque Dither Clipで描く短寿命・衝突なしEffect。即時clip中のTriangle崩壊には使用せず、汎用ローポリ破片はFallback |
| RenderFragment | 実表示Mesh切断後の連結な表示成分。論理Convexとの対応確定までは塊として表示し、幾何寸法だけではデブリ化しない |
| LogicalConvexFragment | 自前Convex切断で生成されるcook前の論理物理成分。RenderFragmentとの対応判定には使用できるが、まだUnity Colliderとして適用済みとは限らない |
| PhysicsRepresentationStatus | RenderFragmentとLogicalConvexFragment集合の対応状態。`Pending=0`／`Represented=1`／`Missing=2`／`Shared=3`／`Ambiguous=4`で固定し、defaultは物理Commit禁止のPendingになる |
| SharedResolutionRole | Shared連結成分内のRenderFragmentへ付けるRole。`None=0`／`Keeper=1`／`DebrisCandidate=2`／`PreserveFallback=3`で固定し、Shared以外はNoneとする |
| RenderFragmentLocalId | ObjectId＋ObjectGeneration内だけで一意かつ非再利用とする正のintのRenderFragment識別子。0は未設定用に予約し、TaskIdとは独立 |
| LogicalConvexFragmentLocalId | ObjectId＋ObjectGeneration内だけで一意かつ非再利用とする正のintのLogicalConvexFragment識別子。0は未設定用に予約し、TaskIdとは独立 |
| SharedGroupLocalId | Shared対応グラフの連結成分を識別する正のint。0は未設定用に予約し、ObjectId＋ObjectGeneration内で一意かつ同一世代中は解体後も再利用しない |
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
| CapCompatibilityKey | 全World Cut Plane、Side／半空間、分離Offset、Cap Material／Debug／Fade状態を正規化して表す、Stencil和集合共有の互換Key |
| Winding Count Stencil | Cut ShellのFront／BackでStencilをIncrement／Decrementし、複数物体が重なっても非ゼロ内部Countを維持する方式 |
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
| Convex Job Pipeline | Native Convex B-repをCount／Write／Validation Jobで平面分割し、MeshData公開後に`Physics.BakeMesh` Jobを接続してCollider Commitへ渡す処理列 |
| Temporary Low-Poly Proxy | Stable Geometry／Colliderが未完成または検証失敗の間に使う、低Triangle表示形状、簡易Convex、Compound Primitive、汎用ローポリFallbackの総称。各実装品質段階の正しさをT-077、生成費用をT-076で測る |
| Geometry／Cook Microbenchmark | 表示Mesh切断、Convex切断、Temporary Low-Poly Proxy、cookを固定Datasetで工程別に測り、計算KernelのSingle-Thread µs/op、Bake／Commit単発Latency、Job Batch Throughput／End-to-End latencyから容量式を作る性能検証 |
| GeometryBenchmarkRunManifest | Cook ProbeとGeometry／Cook Microbenchmark専用のversion付きcanonical JSON。1 Manifestは単一DatasetCaseIdの固定規模軸と、単一Target／Stage／ExecutionMode／CookingProfile／Metric／Unitの1測定系列を表し、BenchmarkSuiteIdで複数系列を束ねる。同一SuiteではDatasetIdからDatasetContentSha256への写像を一意にする。Target×Stage×Mode、全propertyの型・値域・null条件・順序を固定し、clean Repositoryだけ保存を許可して既存TraceRunManifestを拡張しない。v1のLoader上限は64 KiB |
| DatasetCaseId | DatasetContentSha256で固定されたDataset内の1入力caseを識別するID。早期Licensed Fixtureでは`SourceFixtureId.TierToken.VariantId`を使い、Render／Solid／Convexの同名Variantを分離する。同一Suiteでは1つのDatasetIdに1つのDatasetContentSha256だけを許可し、同じcaseの規模軸を不変とする。Manifestの説明変数とResultの測定値をjoinして容量式へ使用する |
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
| Early Licensed Fixture | Phase 0.2でライセンスAsset群へ共通簡易Presetを適用し、個別修理なしで自動選抜したRender／Solid／ConvexテストGeometry。製品用変換済みAssetや全Asset対応の証拠ではない |
| LicensedRepresentative Dataset | Early Licensed Fixtureを同じHarnessで測る非公開の補助Dataset。公開合成Fixtureのcanonical結果が実Asset傾向から大きく外れないか確認するために使い、入力GeometryとAsset対応は公開しない |
| EarlyFixtureSelectionProfile | Phase 0.2のAsset Category集合、Source Triangle Band境界、epsilon、穴封鎖、Bounds／表面／体積品質、Solid／Convex Gate、決定論的入力上限、資源上限、再試行を固定するversion付きcanonical JSON。Profile hash変更で派生Fixtureを無効化する |
| EarlyFixtureSourceCatalog | Blender Import前に匿名SourceFixtureId、AssetCategory、正規化SourceRelativePath、Source file hashを固定するcanonical非公開Catalog。Import前失敗でも投入母集団を復元でき、canonical bytesはSource Bundleへ含める |
| CanonicalBundleIndex | Source／Script／Presetの展開済み通常fileを正規化相対path、byte長、raw content hashで列挙するversion付きcanonical Index。空directoryやtimestampを無視し、symlink等を拒否する。Index bytesのSHA-256を各Bundle Content SHA-256とし、Verifierが実rootの欠落／余分file、長さ、hashをBlender前とReceipt前に完全照合する |
| ZantetsuCanonicalGeometry | Phase 0.2のRender／Solid Triangle MeshまたはConvex Setを、meter／Y-up／左手系、正規化binary32位置、決定的なposition／face／hull順で保存するversion付きcanonical binary。v1は切断／Cook Benchmark用の形状Topologyだけを持ち、拡張子は`.zcg`、decode後の再serialize一致を必須とする |
| SolidSignedVolumeV1 | ZCG decode後のSolid連結成分について、成分Bounds中心、canonical Triangle／成分順、triangleごとの除算、binary64左畳みを固定して正体積を判定する唯一のvolume契約 |
| SolidGeometryValidatorV1 | ZCG bytesを読み、Solid Topology、`SolidSignedVolumeV1`、`ClosedTriangleDistanceV1`を同一artifactで検証するversion固定Validator。Script Bundleで内容を固定し、Blender HarnessとUnity Editor Harnessが共有する |
| SolidCandidateBvhV1 | binary32 decode後Triangleのepsilon拡張AABBから固定axis／median規則で構築し、自己交差の一意候補pairだけを生成する決定論的BVH。候補はcanonical順へsortし、Profile上限超過を`CandidatePairLimit`として停止する |
| ClosedTriangleDistanceV1 | 全Triangle pairを、固定順のpoint-to-closed-triangle／segment-to-closed-triangle／closed-segment距離候補と`epsDistance`で保守的に分類するSolid自己交差predicate。共有indexがあるpairも除外せず、`SharedSimplexResidualV1`で共有simplex近傍外の残余交差を検査する |
| Early Fixture Reduction Variant | 同じSource Fixtureから固定Direct Decimate Presetで作るOriginal／Tri100／Tri500／Tri1000／Tri2000／Tri5000。DatasetCaseIdで区別し、実入力Triangle数をBenchmark Manifestへ、Source／Target／Ratio／Appliedを選抜Reportへ記録する。Voxel／Convex削減系列とは別物 |
| Early Fixture Voxel Variant | Sourceを相対Voxel SizeのVoxel64／128／256でTopology再構成した基底と、その限定Post-Decimate。Triangle差が小さくても基底を保持し、`vox128.base`等のVariantIdとTier付きDatasetCaseId、Voxel Size、形状偏差、Solid検証を記録する |
| EarlyFixtureSelectionReport | Phase 0.2の全Entryについて、version、Profile／Source／Blender／Script／Preset hash、AssetCategory、Profile固定SourceTriangleBand、Status、Process Mode、形状統計、最大2件の固定順Attempts、Resource状態、最終Reject Stage／Reasonを記録するcanonical非公開レポート。Attempt時間／Peak Working Setは観測値でありDataset hashへ含めない |
| LicensedRepresentativeDatasetIndex | Selected／BenchmarkOnly GeometryだけをTier付きDatasetCaseId順に列挙し、Profile／Source Package／Blender／Script／Preset、Geometry Format／Version／RelativePath／ByteLength／canonical Content hashを固定する非公開Index。Geometry rootの完全な通常file許可リストでもあり、このcanonical bytesのSHA-256をGeometryBenchmarkRunManifest.DatasetContentSha256とする |
| LicensedFixtureSelectionReceipt | SelectionRunId、DatasetId、ReportContentSha256、DatasetIndexContentSha256、DatasetContentSha256を結び、ReportとIndexのcanonical検証後に最後に原子的確定する小さなcommit marker。欠落・不一致時は選抜RunをBenchmarkへ渡さない |
| Preprocess Recipe | Assetごとの包含・除外部品、封鎖、空洞保持、分割、Voxel品質を記述する設定 |
| Preprocess Cache Key | 入力、Recipe、Script、Blender版のハッシュから生成する再構築判定値 |
| Boundary Loop | 片面または開放Meshで、1面だけに属するEdgeが形成する穴の輪郭 |
| Voxel Closing | 体積を膨張後に収縮してVoxel数個以下の隙間を閉じる形態学的処理 |
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

利用可能な計算猶予は概ね「Latch後に残るExtending時間＋SlashFrontの飛翔時間」である。近距離では初期前縁の即時命中による低遅延を優先し、遠距離ほど長い猶予を投機切断へ利用する。先行評価は総計算量を消さないため、Candidate Flight Boundsの候補数上限、命中確率、締切順優先キュー、進路外となった未Schedule候補の取消を必須とする。Schedule済みJobは中断せず、完了後にGeneration／前提検証で破棄する。

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
| 既知のAnimation Clip | 予測専用PlayableGraphまたは事前サンプル姿勢 |
| 接触・転動・Jointあり | 局所Prediction Physics |
| ユーザー／スクリプト依存 | 入力を複製できる範囲だけ投機評価 |

稼働中Animatorを未来へ進めて巻き戻さず、副作用のない予測グラフを用いる。接触時にAnimation State、正規化時間、代表骨姿勢が許容範囲外なら成果物を破棄し、実姿勢をフリーズして通常の後追い切断へ戻す。

### 19.4 局所Prediction Physics

通常世界とは別の`PhysicsScene`に、対象Rigidbody、到達までに接触し得る近傍Rigidbody、周辺静的Collider、Joint、必要な外力からなる局所物理島を複製する。固定時間刻みで斬撃波の到達予定時刻まで手動シミュレーションし、その未来姿勢から切断を開始する。

- 静止・解析予測で足りる対象はPhysicsSceneへ入れない。
- 予測シーンはプールし、同じ斬撃波の候補間で共有する。
- 未来ステップは複数フレームへ分散し、スパイクを避ける。
- ユーザー介入、範囲外衝突、スクリプト外力、Animation遷移、別切断を無効化要因として記録する。
- 完全な決定性に依存せず、実接触時に位置差、回転差、対象・Mesh・Physics・Animationの各Generationを照合する。

### 19.5 スケジューリングとCommit

優先度は到達締切までの残り時間、未完了依存、推定計算費用、命中確率、フォールバック時の一時描画費用から決める。遠距離候補は空き時間で処理し、近距離候補は締切を優先する。

投機ジョブは`SlashId`、`SlashGeneration`、命中した`FrontEdgeId`、確定した`SlashFrame`、`ObjectId`、`BaseObjectGeneration`、表示Mesh・物理・Animation・MobPlanの各Generation、予測到達時刻を保持する。Commitには対応するFrontEdge Sweepの`HitConfirmed`を必須とし、識別子、切断面、世代、予測前提のいずれかが一致しない結果は適用せず回収する。これにより、Candidate Flight Boundsへ入っただけの空振り候補や、古い非同期結果が新しい切断状態を上書きすることを防ぐ。

## 20. モブ未来計画とAI LOD

### 20.1 責務分離

UnityのNavMesh、Animation、Behavior系機能は現在状態の実行に利用するが、それらをそのまま未来へ進めたり巻き戻したりしない。ゲーム側に副作用のない`Mob Future Planner`を設け、高水準Intent、NavMesh経路、速度プロファイル、Root軌道、Animation位相を数値データとして一定期間先まで焼き込む。Future Evaluation Schedulerはこの計画を読み取り、斬撃波の到達予定時刻におけるモブ姿勢と切断候補を先行評価する。

### 20.2 MobPlanデータ

`MobPlan`は最低限、次を保持する。

```text
MobId / PlanGeneration / RandomSeed
CreatedAt / StartTime / CommittedUntil / PlanHorizon
Intent / Preconditions / InvalidationReasons
NavMeshPathCorners / SpeedProfile / RootTrajectory
AnimationClipId / Phase / PlaybackRate
SpaceTimeReservations
```

`CommittedUntil`までは、計画を変更するとプレイヤーから不自然に見える範囲として原則維持する。ただしプレイヤー接近・攻撃、経路遮断、モブ自身の切断、予約衝突など安全性やゲーム応答を優先すべき事象では即座に無効化できる。再計画時は`PlanGeneration`を進め、旧計画へ依存する未来姿勢と切断成果物をStaleにする。

### 20.3 プレイヤー介入時間によるAI LOD

距離だけでなく、プレイヤーが移動・斬撃波・その他の操作でモブへ影響できる最短時間を`MinInterventionTime`として推定し、計画Tierを切り替える。

| Tier | 状態 | 計画方針 |
| --- | --- | --- |
| Near | 介入が目前 | 毎フレームに近い通常AIと短いHorizon。プレイヤー反応を優先 |
| Mid | 数秒の猶予 | 経路とAnimationを短区間確定し、定期再計画 |
| Far | 十分な猶予 | キネマティックなRoot軌道とAnimation位相を長めに焼き込み、粗い時空間予約を使用 |
| Dormant | 介入困難・非表示 | 低頻度のIntent／経路計画だけを保持し、必要時まで詳細姿勢を遅延生成 |

Far／Dormantでは個々のRigidbodyや完全な群衆衝突を先読みせず、NavMesh上の経路区間と粗いセル時間帯を予約する。近づくほど予約を解き、通常AIと局所Physicsへ段階的に移行する。Tier切替時にRoot姿勢、速度、Animation位相を引き継ぎ、見た目のポップを避ける。

### 20.4 切断投機との統合

斬撃波候補がモブへ到達する時刻を`MobPlan`上でサンプルし、予測姿勢のSkinned Mesh焼き込み、切断面適用、骨Physics Proxy分類を先行できる。成果物は`MobId`、`PlanGeneration`、`ObjectGeneration`、Animation状態、予測姿勢を保持し、実命中時にすべて検証する。計画が維持されていれば遠距離ほど完成済み成果物を再利用でき、介入で計画が変わった場合は即時レンダラと実姿勢からの後追い処理へ戻る。

計画生成自体がフレーム予算を圧迫しないよう、Mob Future PlannerもFuture Evaluation Schedulerの優先度付きTaskとして実行する。近距離の応答、到達締切の近い切断、物理Commitを優先し、遠距離MobPlanの延長は余剰時間で行う。

### 20.5 無効化と観測

主な無効化要因は、プレイヤーの介入可能領域への侵入、NavMesh変更、経路上の新障害、別モブとの予約競合、Behaviorの高優先Intent、Animation遷移、外力、対象の切断である。`MobPlanCreated`、`MobPlanExtended`、`MobTierChanged`、`ReservationCreated`、`MobPlanInvalidated`、`MobReplanned`、`MobPredictionUsed`、`MobPredictionRejected`をTraceへ記録し、`MobId`と`PlanGeneration`から依存Taskを辿れるようにする。

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

最低限記録するイベントは、`BladeTrackingLost`、`BladeTrackingRestored`、`BladeSamplesReset`、`EdgeGateEntered`、`EdgeGateRejected`、`SlashPrimed`、`SlashLatched`、`SlashFrontCreated`、`FrontVertexAdded`、`FrontEdgeActivated`、`FrontSampleIgnored`、`FrontTopologyRejected`、`SlashFinalizedByReversal`、`SlashFinalized`、`SlashFrontExpired`、`SlashRecoveryStarted`、`SlashRearmed`、`FrontHitConfirmed`、`CandidateDetected`、`TaskScheduled`、`TaskStarted`、`TaskCompleted`、`PredictionValidated`、`PredictionRejected`、`GenerationChanged`、`MobPlanCreated`、`MobPlanExtended`、`MobTierChanged`、`ReservationCreated`、`MobPlanInvalidated`、`MobReplanned`、`MobPredictionUsed`、`MobPredictionRejected`、`CaptureFrameQueued`、`CaptureFrameEncoded`、`CaptureFrameDropped`、`CaptureRingFrozen`、`ProjectionCaptureCopied`、`CommitStarted`、`CommitSucceeded`、`CommitRejected`、`FallbackActivated`、`TaskCancelled`、`ResultDisposed`とする。支持判定実装時にはappend-onlyで`SupportClassificationPending`、`SupportClassificationRetried`、`SupportClassificationTimedOut`、`SupportClassified`、`AnchoredSplitStarted`、`AnchoredSplitCommitted`、`CutBoundaryDormant`、`CutBoundaryActivated`、`CutBoundarySuppressed`、`SupportResultRejected`、`SupportFallbackActivated`、`LogicalCutOperationCreated`、`LogicalCutOperationChildLinked`、`LogicalCutOperationBoundaryLinked`、`LogicalCutOperationBoundaryEndpointLinked`、`LogicalCutOperationTraceCompleted`、`OperationSupportStateChanged`、`FullyFixedCullInvalidated`、`LogicalCutOperationRejected`を追加する。Trace完全性実装時には`TraceIntegritySummary`をappend-onlyで追加する。Render／Convex対応実装時には`FragmentPhysicsRepresentationClassified`、`FragmentConvexMappingEdge`、`FragmentSharedRoleAssigned`、`FragmentDebrisPromoted`、`FragmentDebrisRejected`、`FragmentPhysicsFallbackActivated`をappend-onlyで追加する。Runtime Arena実装時には`RuntimeDebrisSliceAllocated`、`RuntimeDebrisSliceActivated`、`RuntimeDebrisSliceRetiring`、`RuntimeDebrisSliceReclaimed`をappend-onlyで追加する。既存Event名の`Task`は論理Work Itemを指し、`TaskId`をFragment識別子へ流用しない。`TaskCancelled`は原則としてSchedule前の取消または取消可能なI/O処理にだけ使用し、Schedule済みJobの不採用は`PredictionRejected`／`CommitRejected`と`ResultDisposed`で表す。

`RenderFragmentLocalId`と`LogicalConvexFragmentLocalId`は0を未設定用に予約した正の32bit `int`とし、`ObjectId + ObjectGeneration`をスコープとして種別ごとに一意かつ同一世代内で再利用しない。`SharedGroupLocalId`も0を未設定用に予約した正の32bit `int`とし、同じ`ObjectId + ObjectGeneration`内で一意かつ、連結成分の解体後も同一世代内では再利用しない。`CutOperationId`、`LogicalFragmentLocalId`、`CutBoundaryLocalId`は0を未設定用に予約した正の32bit `int`とし、`ObjectId`の生存期間全体で種別ごとに一意かつ非再利用とする。`LogicalCutOperationCreated`の共通`ObjectGeneration`は`ParentObjectGeneration`、`Value1`は作成時`SupportGraphGeneration`を格納し、どちらも宣言型`uint`の全域を許可する。その他のOperation系Eventの共通`ObjectGeneration`はEvent発生時の現世代を記録する。Traceの`ObjectId`と各Cut系LocalId、または`ObjectId`／`ObjectGeneration`とRender／Convex系LocalIdを組み合わせて対象を一意に復元する。doubleへ格納するID、序数、件数は非負int範囲、Generationは`uint`全域とし、いずれもIEEE 754 binary64で整数精度を失わない。イベント別の固定フィールド割当は次を正本とし、汎用的なFrom／To State遷移と混同しない。

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
| `FragmentDebrisPromoted` | RenderFragmentLocalId | PhysicsRepresentationStatus | `None` | Triangle数 | 推定体積 |
| `FragmentDebrisRejected` | RenderFragmentLocalId | PhysicsRepresentationStatus | Reject理由。`None`禁止 | Reasonが示す測定値／Score | 比較閾値 |
| `FragmentPhysicsFallbackActivated` | RenderFragmentLocalId | PhysicsRepresentationStatus | Fallback理由。`None`禁止 | 対応Convex数 | Fallback種別 |
| `RuntimeDebrisSliceAllocated` | RenderFragmentLocalId | `RuntimeDebrisSliceState.Allocated` | `None` | DebrisEventId | 割当Byte数 |
| `RuntimeDebrisSliceActivated` | RenderFragmentLocalId | `RuntimeDebrisSliceState.Active` | `None` | DebrisEventId | 0（予約） |
| `RuntimeDebrisSliceRetiring` | RenderFragmentLocalId | `RuntimeDebrisSliceState.Retiring` | `None` | DebrisEventId | 0（予約） |
| `RuntimeDebrisSliceReclaimed` | RenderFragmentLocalId | `RuntimeDebrisSliceState.Reusable` | `None` | DebrisEventId | Retiringから回収までのFrame数 |

Capture Draft Registry実装時には`CaptureFrameAdmissionRejected`を`TraceEventType`へappend-onlyで追加する。これはID発行前の受付拒否専用であり、正のIDを発行済みの処理だけを表す`CaptureFrameDropped`と同じID相関として解釈しない。共通`CaptureFrameId=0`、`Value0=CaptureFrameAdmissionRejectKind`、`Value1=FrameDraftRegistryFull(5)`へ固定する。`CaptureFrameAdmissionRejectKind`は固定値`None=0`、`PendingLimit=1`、`RunEntryLimit=2`とし、0および未知値をEvent生成時にRejectする。

支持判定のReasonには`AnchorGenerationMismatch`、`SupportGraphGenerationMismatch`、`SupportClassificationUnavailable`、`SupportConnectivityAmbiguous`、`InvalidLogicalCutOperation`を追加し、Trace完全性には`TraceWriteFailureObserved`と`TraceCaptureOverflowObserved`を追加する。Render／Convex対応とRuntime Debrisには`FragmentCoverageBelowThreshold`、`FragmentMappingAmbiguous`、`FragmentSharedKeeperUnavailable`、`FragmentProtectedByImportance`、`FragmentProtectedBySize`、`FragmentGenerationMismatch`、`InvalidPhysicsRepresentationState`、`RuntimeDebrisArenaFull`、`RuntimeDebrisFenceUnavailable`、`RuntimeDebrisUploadRejected`を追加する。いずれも既存`TraceReason`の次の未使用値へappend-onlyで明示値を割り当て、既存値を変更・再利用しない。Reject／Fallbackイベントは専用Reasonを必須とし、Reason enumを`Value0`／`Value1`へ重複保存しない。

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

新規開始とRecoveryは、rootを作成／列挙する前に`CaptureStagingBaseRoot/.locks/run-{runId}.lock`と`CaptureFinalBaseRoot/.locks/run-{runId}.lock`の2本をno-followでopenし、各OS handleを`FileShare.None`相当の排他共有Modeで取得する。両lockのabsolute pathをOSの正規化済みfull pathへ変換し、まず`OrdinalIgnoreCase`、同値時はordinalで比較した昇順へsortして、すべてのCoordinatorが同じ順で取得する。正規化後に同一となるlock pathは構成不正としてRun開始前にRejectし、暗黙に1本へ縮約しない。lock directory／fileは各信頼base root直下の固定名だけを許し、reparse pointを拒否する。

取得は非待機とし、2本目を含む途中の取得に失敗した場合は取得済みhandleを逆順に直ちに解放し、staging／finalのどちらのRun rootも作成、列挙、変更しないで`RunAlreadyOwned`としてbackpressureする。両handleの取得成功だけがRun root一組の排他的所有権を与える。Coordinatorは初期化からCaptureComplete後のstaging cleanupまたは明示abortまで両handleを保持するため、異なるstaging baseから同じfinal base／TestRunIdを狙うCoordinatorもfinal側lockで排除される。lock fileの存在や内容は所有権の証拠にせず、取得中handle集合だけを正本とする。プロセス終了／crashではOSが両handleを解放し、残った固定lock fileは次回同じ順序で再openできる。

両lock取得後に暗号学的乱数128 bitの小文字hex 32桁`RunInitializationId`を発行し、両Run rootを次の順で二相初期化する。`staging root作成 -> staging/run.init.tmp書込・flush・run.initへRename・directory flush -> final root作成 -> final/run.init.tmp書込・flush・run.initへRename・directory flush -> 両init照合 -> stagingとfinalへrun.ready.tmpを書いてflush・run.readyへRename・各directory flush`とし、両`run.ready`確定後だけ新規Capture受付を許可する。`run.init`はcanonical Schema v1で`SchemaVersion`、`TestRunId`、`RunInitializationId`、`RootRole`（`Staging`または`Final`）、`StagingRunRootSha256`、`FinalRunRootSha256`をこの順に持つ。Root hashは信頼baseから導出・正規化した各absolute Run rootのUTF-8 bytesに対する小文字SHA-256とする。`run.ready`はSchemaVersion、TestRunId、RunInitializationId、StagingInitSha256、FinalInitSha256をこの順に持ち、両rootで同一canonical bytesとする。両SchemaはPlanと同じUTF-8／BOMなし／空白なし／最短integer／literal ASCII／再serialize完全一致規則、最大4 KiBと必須の呼出側byte上限を使う。tmpは権威を持たず、init／readyだけが相互bindingの正本となる。

両lock取得後のRecoveryでは両rootを同時に調査する。一方だけが存在して有効な`run.init`を持つ場合、marker内の両Root hashと導出rootが一致し、既存rootに初期化許可file以外がなければ、同じRunInitializationIdで欠けたpeer root／init／readyを作って初期化を完了する。ただしstaging rootがなく、final rootに有効なinit／ready／`capture.index`とIndex記載の全Artifactが揃う場合は完了後cleanup済みの正常状態であり、staging rootを再作成しない。両rootに一致するinitがありreadyが片側／両側で欠ける場合も同じbytesのreadyを補完する。root作成後marker書込前にcrashした空directory、または非権威な`run.init.tmp`／`run.ready.tmp`だけを持つdirectoryは、排他lock集合、no-follow、導出path、空／tmp-onlyを確認して削除し同じRecoveryを再開できる。markerなしで他fileを持つroot、init／readyのTestRunId、InitializationId、Role、Root hash、相互hashが不一致なrootは削除／上書きせず`RunRootCollision`として両rootを隔離する。既存の完全初期化rootは、その後Plan／IndexとTrace ManifestのTestRunId、RunInitializationId、Manifest hashが一致する明示Recoveryにだけ開く。

許可file集合は、staging Run root直下では`run.init`、`run.ready`、各`.tmp`、`publication.plan`／`.tmp`、固定`frames` subtree、final Run root直下では`run.init`、`run.ready`、各`.tmp`、`capture.index`／`.tmp`、固定`frames` subtreeだけとする。lock fileはRun root外の`.locks`だけに置く。初期化完了後の未知file、別Run marker、別Coordinatorによる同時所有はFail Fastし、既存fileを変更しない。これによりRunごとに1から再開する`CaptureFrameId`、固定`frames/{id}`、`publication.plan`、`capture.index`が他Runと衝突しない。

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
