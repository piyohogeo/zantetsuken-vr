# VR斬鉄剣ゲーム 技術設計書

*即時シェーダ切断と非同期メッシュ／物理更新による、低遅延・反復切断パイプライン*

| 項目 | 内容 |
| --- | --- |
| 文書目的 | Codexで継続更新するプロジェクト設計上の正本 |
| ステータス | Draft v1.5 / PoC実装準備・固定Capture Profile／同期映像／未来評価設計段階 |
| 作成日 | 2026-08-21 |
| 最終更新 | 2026-08-26 |
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

- 生涯切断数ではなく、未確定のPending Cut数が描画コストを決める構造にする。

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

> **状態モデル** 各切断対象は Stable Geometry と Pending Cuts を持つ。バックグラウンド処理が完了した切断はStable側へ焼き込み、Pending一覧から除去する。

### 4.1 コンポーネント境界

| サブシステム | 責務 |
| --- | --- |
| Blade Pose Adapter | OpenXR Grip Poseへ持ち手別のGripToKatanaOffsetを適用し、BladeAxis、EdgeDirection、SideNormal、追跡有効性を提供 |
| Blade Sweep Detector | 刀身の連続姿勢からswept volumeとGesture Sampleを構築し、速度・移動量・Edge Direction Gateを評価。対象への最終命中は確定しない |
| Cut State | Stable世代、Pending Cut列、論理破片、ジョブ状態、上限管理 |
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

- 実命中時に`HitConfirmed`を記録し、対象の`ObjectGeneration`を更新してPending Cutを追加する。同フレームからシェーダで正負側をclipし、仮断面、切断縁、音、火花、Hapticsを開始する。

- 投機成果物が命中したSlash／Segment、確定した`SlashFrame`、基底対象世代、予測姿勢と一致すれば、表示・物理成果物を描画フレーム／物理ステップ境界でコミットする。

- 投機成果物が未完成または検証不一致なら、実命中時の状態を基底として表示MeshとConvex切断を優先ジョブへ投入する。即時表示は完了まで継続する。

- 実ジオメトリへ置換できたPending CutをStable Geometryへ焼き込み、Pending一覧から削除して一時描画コストを回収する。

### 4.3 バックグラウンド実行モデル

フレーム内または複数フレームにまたがるCPU計算は、C# `Task`を大量発行せず、Unity C# Job SystemとBurstを基本とする。メインスレッドはUnity Objectを数値スナップショットへ変換し、締切と優先度に従ってJobをBatch Scheduleする。Job本体は`NativeArray`、`NativeList`、`NativeStream`等のアンマネージデータだけを扱い、GameObject、Component、Transform、Renderer、Rigidbodyを直接操作しない。

- Job向き：候補交差、三角形分類、表示Mesh切断、Convex平面クリップ、断面・質量特性生成、未来軌道／MobPlanのBatch評価、対応APIによるCollider Bake。

- メインスレッド向き：JobのSchedule、`JobHandle`依存関係、世代／命中検証、`MeshData`のMeshへの適用、Renderer参照差し替え、Rigidbody／Collider生成、描画フレーム／物理ステップ境界のCommit。

- `Task`／Unity `Awaitable`向き：ファイルI/O、Trace／録画保存、Editorツール、外部プロセス待機、Unity非同期APIの進行制御。CPU幾何計算の標準実行基盤にはしない。

極小Jobを対象ごとに無制限発行せず、同種処理を`IJobFor`／`IJobParallelFor`等でBatch化する。JobはSchedule後に中断できないため、投機前提が崩れた場合もメインスレッドから`Complete`を強制せず、完了後にGeneration不一致として破棄する。`TaskId`はC# `Task`型を意味せず、Job、I/O、GPU処理を含む論理Work Itemの相関IDとして維持する。

## 5. 即時表示レンダラ

### 5.1 分離表示

元メッシュを論理破片ごとに描画し、各切断面の正負符号に応じてフラグメントをclipする。単一切断では正側・負側の2インスタンスを描き、平面法線の正負方向へ小さく移動して隙間を作る。複数切断では、論理破片が保持する半空間の組み合わせだけを描画する。

破片の表示オフセットはスキニング後またはワールド変換後に加える。スキニング前に加えると、ボーン姿勢によって分離方向が歪むため避ける。

### 5.2 仮断面とステンシル

ステンシルは切断そのものではなく、仮断面キャップのマスク生成に使う。プリプロセス済みSolid Cut Meshまたは直前のStable Cut Shellから、現在のPending Cutを適用した実行時Cut Shellを導出する。その閉じたCut Shellの表裏面から内部領域をStencilへ記録し、対象のローカルOBBと切断平面の交差から作る有限な`Cap Bounds Polygon`をStencil非ゼロ領域だけ描画する。

- clip()：物体を正負に分け、隙間の空いた分離表示を作る。

- Stencil：切断平面上で元物体内部に相当する範囲をマスクし、仮断面を塗る。

- 実断面Mesh：バックグラウンド処理完了後に仮断面を置換する。

- Cap Bounds PolygonはOBBの12辺と切断平面を交差させ、epsilonで重複を除いた3～6頂点を平面上で並べて生成する。複数Pending Cutでは、ほかの切断平面が定める論理破片の半空間で凸多角形clipし、切断面同士の交差を即時表示へ反映する。

- Cap Bounds Polygonは物体表面との正確な交差輪郭ではないため、最終的な凹形状、穴、部品輪郭はStencilで制限する。実表面との輪郭を三角形化できた場合は実断面Meshとして扱い、Stencilへ重複して依存しない。

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
| 通常グレー | 表示Mesh、実断面、Colliderが確定し、Pending CutがないStable状態 |

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

- 同一物体のPending Cut数に上限を設ける。初期候補は2〜4枚。

- 上限到達時は複数切断をまとめて再構築し、古いPending CutをStable Meshへ焼き込む。

- 画面外・遠距離・停止中の物体を優先的に確定する。

- 小さすぎる論理破片は描画／物理の対象から外し、簡易デブリへ統合する。

- Stencilは切断面ごとの一時作業領域として再利用し、恒久的なビット割当は行わない。

### 5.6 スクリーンスペースStencil Batch

Stencil Bufferは画面座標ごとに共有されるため、すべての即時切断物体を無条件に同じStencilへ蓄積しない。ただし、現在の全World Cut Plane、各PlaneのFragment Side／半空間、分離Offset、Cap Material、法線、デバッグ色、Fade等が一致する対象は`CapCompatibilityKey`で同じ互換Groupへまとめる。このGroup内ではキャップ描画結果が同一なので、スクリーンスペースで重なっていてもStencilを和集合として共有できる。

StencilはParityの`Invert`ではなく、整合したCut ShellのFront／Back Faceに対するIncrement／DecrementからなるWinding Count方式を使い、Capは`Stencil != 0`で描画する。閉じた部分ではFront／Backが`+1 - 1 = 0`へ相殺され、切断による開口部だけに非ゼロの`Residual Stencil Support`が残る。途中のStencil書き込みが別物体と重なっても、最終的にゼロへ戻る領域は競合としない。互換Group内で複数物体の開口部が重なった場合もCountを1、2、3と保持し、偶数重なりを誤って空洞化しない。8bit CountのWrap／飽和条件、面向き、Depth／clipの非対称はT-067で検証する。

各フレーム、互換Groupをノードとし、左眼または右眼のどちらかで保守的な可視Cap Boundsが重なる、かつ`CapCompatibilityKey`が異なるGroup間へ辺を張る`Stencil Conflict Graph`を構築する。物体OBB投影矩形と可視Cap Boundsはどちらも安全側の非交差証明に使い、各眼でいずれかが非交差ならその眼では競合しない。次数または画面面積の大きい順にFirst-Fit Greedy Coloringし、同じColor内では「全眼で可視Cap Boundsが非重複」または「重複してもキャップ互換」のどちらかを保証する。

各Colorについて、対象Rectの予約Stencil領域をクリアし、Color内の全Cut Shellを共通Stencil Volume Phaseへ投入した後、対応する全Cap Bounds Polygonを共通Cap Phaseへ投入する。Color内では非互換な`Residual Stencil Support`同士が重ならないため、Rawな途中書き込みの重なりを許容しつつ、物体別Stencil IDを持たず同じStencil操作を再利用できる。Shader Passは全対象で共通化できるが、Mesh／Material等により各Phaseが複数Drawへ分かれることは許容する。

- Broadphaseでは分離Offsetと安全Marginを含む物体OBBの左右眼投影矩形を使う。重なる組だけ、表向きのOBB切断面から得たCap Bounds Polygonを左右眼へ投影して再判定する。どちらの判定も非交差なら安全という悲観的な証明として扱い、Near Plane交差、Raster／MSAA、頭部移動誤差を考慮してBoundsを保守的に拡張する。

- `CapCompatibilityKey`は順序を正規化した`CutPlaneId`列、Side Mask、Offset、Material／Debug Stateから作り、Raw floatだけをHashの正本にしない。同じSlash由来でも、対象が別々に移動・回転した後は現在のWorld Planeをepsilon比較し、一致しなければ別Groupへ分離する。片方だけに追加Pending Cutがある場合も互換ではない。

- キャップの可視性は元Object単位ではなく、`論理破片 × 切断面`の`CapRecord`単位で判定する。同じ切断面でも正負破片の断面Normalは逆向きになるため、片側が裏向きでも反対側を自動的に省略しない。

- 現在のWorld Cap Planeについて、`dot(CapNormal, EyePosition - CapPoint)`を左右眼で評価する。両眼とも明確に裏向きのCapRecordを除外し、互換Group内の全CapRecordが除外された場合は、そのGroupをConflict Graphへ入れず、Stencil Clear／Volume／Cap処理を丸ごと省略する。片眼だけ表向きならSingle Pass Instanced用Recordを残す。

- カメラが切断面近傍にある場合の左右眼不一致と頭部微動による点滅を避けるため、Facing epsilonと1～2フレーム相当のヒステリシスを候補とする。Frustum外判定も同じ段階で行うが、通常のclip済み破片カラー描画とShadowCasterは消さない。

- Cap処理順は`CapRecord生成 -> 両眼Frustum／Facing Cull -> CapCompatibility Group -> Stencil Conflict Graph -> Greedy Coloring -> Stencil Volume／Cap描画`とする。Backface Raster Cullだけに任せず、Groupを早期除外して対応するStencil仕事も削減する。

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

ColliderのBake／cookingは視覚切断のクリティカルパスに含めない。切断命中フレームから断面と隙間を表示し、ConvexとBakeが間に合わなくても視覚応答を待たせない。切断直後は`PendingPhysicsSplit`へ入り、旧Convexを支持用として持つ1つの`FragmentGroup`の下で、正負側の表示破片を同じRigidbodyへ追従させる。旧Convexを複製して双方へ付与すると、不自然な押し出しや存在しない中央部への接触が発生するため行わない。

- 刀は旧Colliderを含む物理Colliderへ接触させず、Edge Direction Gate成立中の論理SweepだけでHitを判定する。プレイヤーの手・身体が旧Colliderへ触れる場合の例外方針は別途T-005で評価する。

- `PendingPhysicsSplit`中は原則1つのRigidbodyと旧Colliderを物理状態の正本とし、左右の表示破片は独立した接触、落下、回転を行わない。外部から受けた力と接触ImpulseはFragmentGroup全体へ作用する。

- 断面間の小さな見た目上のめり込み、周辺物体と表示破片の一時的なめり込み、見えている切断隙間に旧Colliderが残ることを許容する。違和感を限定するため、Pending中の表示分離量は切断幅を基準とした上限以内に抑える。

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

同じ自前Convex切断結果を入力し、頂点／面数、Cooking設定、検証有無、Allocator、Thread数、Warm-up、Release相当Build、CPU Affinityを可能な範囲で揃える。Unity同梱PhysXとProbe側PhysXの版が一致しない場合は両版をRun Manifestへ記録し、差をAPI経路だけの因果差と断定しない。

計測は少なくとも、自前Convex clipping、Descriptor／MeshData構築、Native境界転送、`ApplyAndDisposeWritableMeshData`、Hull計算、PhysX内部形式生成、Stream処理、`Physics.BakeMesh`、Collider Commitへ分離する。8、16、32、64、128、255頂点級、単発／Batch、同時Slash、Fast Cook／Fast SimulationでP50／P95／P99、Throughput、Worker占有、Main Thread時間、一時／最終メモリ、失敗率、生成頂点／面、接触／Query品質を比較する。

Native PhysXが生成した`PxConvexMesh`またはCook済みBinaryをUnity `MeshCollider`へ注入する公開経路は前提にしない。大差が出ても、まずUnity経路のBatch化、Cooking Profile、入力簡略化、二段階Collider、Cacheで要件を満たせるか確認する。Native採用は、Unity経路のP99が実際にPending／90Hz予算を破り、差が継続的かつ大きく、Unity側で回避不能な工程にあり、Native成果物を実ゲームへ統合する別の小型Prototypeが成立した場合だけ再検討する。この場合はcook関数だけの交換ではなく、切断破片のQuery／接触／Scene同期を含む物理経路の部分置換として見積もる。

### 7.5 微小付属物の消去

Physics Proxyで表現しないアンテナ、細い取手、小装飾などは、プリプロセス時に`Micro Attachment`として本体から識別可能にする。斬撃の切断帯に触れたMicro Attachmentは、極小の表示Mesh／Collider／Rigidbodyを生成せず、`HitConfirmed`と同じフレームで部品全体を不可逆に消去する。即時シェーダで一度切れた部品が実Meshへの差し替え時に復活する挙動は禁止する。

- 切断帯と重ならないMicro Attachmentは、Anchorが属する側の表示破片へそのまま付属させる。

- 切断帯と重なる、両側へまたがる、またはAnchor所属が曖昧なMicro Attachmentは全体を消去する。必要なら同フレームに火花や非物理の小片VFXを出し、粉砕として見せる。

- 消去状態は`AttachmentId`と`AliveMask`でObjectGenerationへ含め、即時表示、バックグラウンド表示Mesh、Cut Shell派生、再切断、保存Traceが同じ状態を参照する。古い成果物による再出現を世代検証で拒否する。

- Micro Attachmentは原則としてPhysics Proxyへ含めない。ゲーム上重要、シルエット上大きい、相互作用対象となる部品はRecipeで除外し、通常部品として処理する。

- 実装を単純にするため、Blender前処理で対象の連結成分を別Renderer／別Componentへ分離する構成を優先する。統合Meshのまま扱う必要があるAssetだけ、頂点／三角形の`AttachmentId`とGPU生存Maskを使用する。

### 7.6 GPU Micro Debris

Micro Attachment消去時は、元部品の実Geometryを事前生成したShard Clusterへ分け、Vertex Pullingと間接描画でGPU上に短時間飛散させる。1体全体が2,000～3,000 Triangle程度というAsset予算から、Micro Attachment 1件は通常20～150 Triangle程度を想定し、シーン全体の通常Active量は数千Triangle以下とする。`HitConfirmed`と同じフレームに元RendererのAliveMaskを落とし、CPUから共有`GpuMicroDebrisSystem`へ発生Event Recordを1件だけ送る。

- Blender前処理で、接続、面Normal、Material、面積上限を基準に隣接2～8 Triangle程度を同じ`ShardId`へまとめる。Triangle単位の紙吹雪感を避け、各Shard内は元Meshの形状を保ったまま共通の並進・回転を行う。小さすぎる部品はTriangle単位でもよい。

- GeometryはVertex Buffer、Corner／Index Buffer、Shard Metadataからなる共有`Debris Geometry Atlas`へ事前登録する。Vertex Shaderは`SV_VertexID`等からCorner、元Vertex、ShardIdを引き、Shard単位のTransformを適用する。Runtime生成された小さな論理破片は表示Mesh切断Jobの出力時にDebris用Corner Streamも生成できるが、転送／Atlas予算超過時は汎用ローポリ破片または即時消去へFallbackする。

- Event RecordはGeometry Offset、発生Transform、切断面法線、親Rigidbodyの点速度、基底色、乱数Seed、開始時刻、寿命を持つ。各ShardにGameObject、Transform、Rigidbody、Colliderを作らず、位置を`p(t) = p0 + v0 * t + 0.5 * g * t^2`、回転をSeed由来の軸・角速度・経過時間からShader内で直接求め、CPUの毎フレーム更新を行わない。

- 全Active Eventを固定長BufferとIndirect Command Bufferへまとめ、同じMaterialでは原則1 Draw、Material差を含めても2～3 Draw以内を初期目標とする。EventごとのBuffer再確保、Geometry再転送、GameObject生成を行わない。

- 破片は親の点速度へ切断面法線方向の初速とSeed由来のばらつきを加えて飛ばす。寿命の初期候補は0.3～0.8秒とし、終了時は半透明BlendではなくZWrite可能なOpaque／Alpha Clipのディザで消滅させる。Shadow Pass、Collider、Light Probe個別更新は持たない。

- ディザ閾値はワールド座標または破片ローカル座標から生成する安定Noiseを使用し、左右眼で同じ表面点が同じ生存判定になるようにする。スクリーン座標だけに依存するランダムディザは使用しない。

- 影、Motion Vector、個別ライト、破片同士と地面の衝突を無効化する。飛散範囲を含む保守的Boundsを持つ共有Batchへまとめ、間接描画またはVFX Graph Mesh Particleで描画する。

- 初期予算は、1 Event 20～150 Triangle、通常Active合計500～3,000 Triangle、品質低下開始5,000～8,000 Triangle、Hard Cap候補10,000 Triangle、Active Event 8～32とする。1～2万TriangleはMicro Attachment通常仕様ではなく、全身／大きめ破片まで流した場合のStress Testに限定する。Triangle数に加えて両眼の画面占有面積とOverdrawを予算化し、超過時は古いEventの寿命短縮、Shard統合、汎用破片、火花／Quad、即時ディザ消去の順で品質低下する。

PoCではVFX Graphで外観、汎用破片Fallback、URP／XR適合性を素早く検証する。実Geometry経路は固定長Event Buffer、Geometry Atlas、Shard Metadata、解析運動Shader、`Graphics.RenderPrimitivesIndirect`／同等APIによる専用Vertex Pulling実装を第一候補とする。GPU Eventなど実験的機能への依存は必須にしない。

### 7.7 全体低重力

空中物体斬りの猶予を自然に増やし、世界全体の挙動を統一するため、個別の空中斬り補助ではなく全体低重力を初期方針とする。PoCの仮値は標準重力の約0.5倍、`(0, -4.9, 0) m/s^2`とするが、最終値はプレイテストで決める。

- `WorldPhysicsProfile`を重力の唯一の設定元とし、起動時に`Physics.gravity`へ適用する。物理予測、解析軌道、GPU Micro Debris、その他の非物理VFXも同じ値を参照し、`-9.81`などを各実装へ直接記述しない。

- 重力値はInspectorまたは開発用設定から変更可能にし、初期比較候補を0.35G／0.5G／0.7G／1.0Gとする。各Runの重力ベクトルとProfile版をTrace／Run Manifestへ保存する。

- PoC初期は反発係数、Drag、切断分離Impulse、モブのジャンプ／落下Animation、破片寿命を低重力専用に作り込まず、既定値または仮値を使用する。実プレイで具体的な違和感が確認されてから個別に調整する。

- `Time.timeScale`による常時スローモーションは重力調整の代用にせず、入力、斬撃波、非同期処理、物理予測の時間軸を通常速度に保つ。PoCでは対象別Gravity Scaleも導入せず、必要性がプレイから判明した場合だけ拡張する。

## 8. 世代管理と非同期制御

各SlashはGestureのLatch時に単調増加する`SlashGeneration`を持つ。各切断対象は確定状態を示す`ObjectGeneration`を持ち、`SlashFront`のSweepによる実命中が確認され、Pending Cutを登録した時点でだけ更新する。空振り、候補列挙、投機ジョブ開始では対象世代を進めない。

投機ジョブは開始時の`BaseObjectGeneration`、`SlashId`、`SlashGeneration`、命中した`FrontEdgeId`、`SlashFrame`を保持する。ジョブを強制キャンセルするのではなく、完成時およびCommit時に、実命中と各識別子・世代・前提条件を検証する。一致しない成果物はコミットせず破棄し、安全に再利用できる中間資産だけを回収する。

| 状態 | 意味 | 許可される処理 |
| --- | --- | --- |
| Stable | 描画・物理とも確定済み | 新規切断の基底に使用 |
| Pending Visual | シェーダ仮表示中 | 追加入力を受け、表示ジョブを更新 |
| Pending Physics Split | FragmentGroupの1 Rigidbody／旧Colliderを共有し、表示と論理破片だけが分離済み | Convex生成とBakeを待ちながら、後続切断と外力を受理 |
| Ready to Commit | 最新世代の成果物が完成 | 境界タイミングで原子的に差し替え |
| Stable Fast Cook | Fast Cook Colliderで物理分裂済み | 通常物理を継続し、必要なら低優先度Upgradeを予約 |
| Physics Upgrade Pending | 別MeshをFast Simulationで再Bake中 | 現Colliderを維持し、世代変更時はUpgradeを破棄 |
| Stable Fast Simulation | Fast Simulation Colliderへ安全に差し替え済み | 長寿命・高接触破片として通常物理を継続 |
| Stale | 完成時点で世代が古い | 適用せず回収、必要なら中間資産のみ再利用 |

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

非公開リポジトリへのアクセスはSyntyライセンス上の許可を持つ開発チームだけに限定する。購入原本は変更せず保存し、展開したFBX／Texture、加工Asset、Solid Cut Mesh、Physics Proxyなどのライセンス派生物も公開Git履歴へ入れない。公開リポジトリから参照する場合も、公開Submodule、公開Release、公開CI Artifact、共有Cacheを経由してAsset本体を配布しない。

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
| D-067 | cooking非同期化 | Collider Bake／cookingを視覚切断のクリティカルパスから外し、完了前でも命中フレームから断面と隙間を表示する | 確定 |
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
| D-078 | 有限仮キャップ | 即時キャップ板をローカルOBBと切断平面の3～6頂点交差多角形から生成し、他のPending Cut半空間でclipしてからStencilで実輪郭へ制限する | 確定 |
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

## 13. 未決事項

| ID | 論点 | 選択／質問 | 影響 | 決定時期 |
| --- | --- | --- | --- | --- |
| O-001 | 初期ターゲット | 解決済み：PCVRを採用（D-011） | Quest単体は当面スコープ外 | 2026-08-21 |
| O-002 | 目標FPS | 解決済み：両眼描画90fpsを基準（D-012） | 再投影は安全網として扱う | 2026-08-21 |
| O-003 | Pending上限 | 同一物体2、3、4枚のどれを標準とするか | 描画コストと連続斬り感 | T-003後 |
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
| O-031 | GPU Micro Debris予算 | 通常500～3,000 Triangle、品質低下開始5,000～8,000、Hard Cap候補10,000、Active Event 8～32、寿命0.3～0.8秒を初期値とし、画面占有面積／Overdraw、Shard Cluster寸法、Geometry Atlas容量、Draw上限、Runtime Geometry転送条件を決める | GPU時間、Draw／Batch、Buffer転送・メモリ、見た目の密度 | T-063後 |
| O-032 | 最終重力と周辺調整 | 0.35G／0.5G／0.7G／1.0Gの採用値と、反発、Drag、分離Impulse、Animation、破片寿命の追加調整要否 | 空中斬り成功率、世界の重量感、テンポ、物理安定性 | T-064のプレイテスト後 |
| O-033 | Shadow近似品質 | 両面・キャップなし近似を許容する距離／時間、Stable専用Shader分離、問題時の簡易Shadow Cap導入条件 | Shadow GPU時間、Draw、接地影、Self Shadow、実装複雑度 | T-065後 |
| O-034 | Stencil Batch予算 | 最大Color数、OBB／Cap Bounds Margin、World Plane一致epsilon、Facing epsilon／ヒステリシス、Stencil Clear／Count方式、相殺不成立時のFallback、上限超過時にキャップを省略する距離／画面寸法 | CPU分類・彩色時間、Stencil GPU時間、Draw、仮断面品質 | T-066～T-068後 |
| O-035 | Job実行予算 | フレームごとのSchedule数、Batch Size、Worker占有上限、複数フレームJobのNativeメモリAllocator／寿命、MeshData一括Commit数、Bake同時実行数 | 90fps安定性、投機完了率、Pending滞留、メモリ | T-069後 |
| O-036 | Native Cook再検討閾値 | 「継続的に大きい差」の倍率、Unity Bake P99／Pending許容時間、Worker占有、Native部分置換へ進む最低改善量と保守工数上限 | Backend選択、実装規模、Unity更新追従、再現性 | T-070とPhase 4実測後 |
| O-037 | Surface Projection閾値 | Trusted Exterior分類、最大距離、法線内積、包含Margin、最小厚み、Reduction前後の再Projection条件、自己交差検出精度 | Silhouette回復、Solid堅牢性、自動成功率、前処理時間 | T-071後 |

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
| T-019 | Trace完全性 | Slash生成からCommit／破棄までIDと状態遷移を欠落なく追跡できる | 正常、未Schedule取消、Schedule済みJobのGeneration Reject経路を自動照合 |
| T-020 | Trace負荷 | 記録有効時も90fps予算とJobタイミングを実用範囲で維持 | 無効／有効時のCPU、GC、メモリ、イベント欠落を比較 |
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
| T-063 | GPU Micro Debris | 実GeometryのShard Clusterが連続消去でもGameObject／Rigidbody／ColliderとGCを増やさず、通常数千Triangleを少数Drawで両眼安定描画し、予算超過時に段階的Fallbackできる | 1 Event 20～150、Active合計500～3,000、5,000～8,000、10,000、Stress 1～2万Triangleを比較し、Shard数、寿命、画面占有、Overdraw、Vertex／Pixel GPU時間、CPU時間、Draw、Atlas／転送メモリ、GC、左右眼ディザ差、Fallback順を測定する。Triangle単位と2～8 Triangle Clusterの見た目も比較する |
| T-064 | 全体低重力プレイ | 一般プレイヤーが空中物体を狙いやすく、世界全体の浮遊感とゲームテンポが許容でき、全軌道系で重力が一致する | 0.35G／0.5G／0.7G／1.0Gを同一投擲・切断Scenarioで比較し、滞空時間、斬撃成功率、主観評価、Physics／予測／GPU破片の軌道差を記録 |
| T-065 | 即時切断Shadow | Stencil Capなしの両面Shadowが即時状態で許容でき、clip／Offsetがカラー像と一致し、片面／両面群分割が90fps予算を阻害しない | 箱、薄板、凹形、非閉形状を床／壁近傍で切り、Directional各Cascade、Point、Spot、Bias条件について実Capとの差分、漏れ、peter-panning、Shadow Draw、GPU時間を比較 |
| T-066 | Stencil彩色Batch | 非互換な可視Cap Boundsが左右眼のいずれかで重なる対象だけを別Colorへ分離し、OBB投影またはCap Boundsの非交差で安全と証明できる対象を同一Colorへまとめ、Stencil混入なしでCPU／GPU予算内に収まる | 左右眼だけでCapが重なる配置、OBBは重なるがCapは非交差の配置、Near Plane交差、全Cap重複、非重複、多数Pendingを生成し、Conflict Graph、Color数、Stencil差分、彩色CPU、Clear／Volume／Cap GPU、Draw、Fallbackを測定 |
| T-067 | Stencil相殺・互換Group | 整合したCut Shellの閉部分ではFront／Backがゼロへ相殺され、非ゼロ領域が可視Cap Bounds内に収まる。キャップ互換な重複対象は同一Colorで正しく和集合表示され、不一致は確実に別Groupとなる | 同一Slashの静止／共通親／別Rigidbody、追加Cut、異Material、Debug色差、偶数重なり、多重Countに加え、面向き不正、非閉形状、Near Plane、非対称clip／Depth、MSAA境界を作り、残留Stencil範囲、Key分類、World Plane epsilon、画像差、Fallback、Color削減率を検査 |
| T-068 | 両眼Cap可視性Cull | 両眼とも裏向きの互換Groupだけが安全に早期除外され、片眼可視、面近傍、正負破片でCap欠落や点滅を起こさずStencil仕事を削減する | 左右眼でFacingが一致／不一致となる配置、面横断、頭部微動、正負Cap、Frustum外を再生し、Cull判定、ヒステリシス、Stencil Draw／GPU時間、左右眼画像差を比較 |
| T-069 | Convex Job Pipeline | Convex分割と複数`Physics.BakeMesh`がメインスレッドを停止させず、世代不一致成果物を適用せず、Pending物理共有から安全に分裂できる | 破片数、面数、同時Slash数、Fast Cook／Fast Simulationを変え、各Job段階時間、Schedule数、Worker占有、Main Thread Commit時間、Bake P50／P95／P99、Generation Reject、物理差し替え時Impulseを測定。同一Mesh同時Bakeを不変条件として検出する |
| T-070 | Unity／Native Cook Probe | U1／N1／N2／N3を同一入力と近似条件で再現測定し、Unity経路の実費用、Hull再計算の寄与、完全Topology／直接生成の改善上限を工程別に説明できる | 8～255頂点級、単発／Batch、Fast Cook／Fast SimulationをRelease相当で反復し、P50／P95／P99、Throughput、各工程時間、Thread占有、メモリ、失敗率、出力形状、接触／Query品質、Unity／PhysX版をRun Manifestへ保存する。版違いと非利用可能なNative生成物を明記する |
| T-071 | Surface Projectionと自己交差 | Voxel形状より主要Silhouette／曲面誤差を改善しつつ、Projection／Reduction後も自己交差、面反転、退化、境界、体積異常を残さず、実Mesh切断の単純Loop前提を満たす | 車、建物、家具、薄板、近接二重面、内部装飾を含むDatasetでProjectionなし／無制約Shrinkwrap／制約付きProjectionを比較し、距離分布、Silhouette、Normal、包含、最小厚み、自己交差、投影拒否率、Triangle数、前処理時間、多方向切断Loop次数と三角形化成功率を測定する |

## 15. 実装ロードマップ

| 段階 | 焦点 | 主要成果物 | 完了条件 |
| --- | --- | --- | --- |
| Phase 0 | 非VR基盤・観測 | Unity 6.3 LTS 6000.3.22f1、Universal 3D／URP、Repo・ignore・Package Lock、固定テスト、Editor更新手順、入力抽象化、WorldPhysicsProfile、ProfilerMarker、Flow、TraceLogger、最小タイムライン、FrameId同期のUnity選択的キャプチャ | 固定Editor版から非VRで再現可能な性能基準、重力Profile、Work Item／Job時系列、対応画像を取得し、一時worktreeで更新・復帰手順を確認 |
| Phase 0.25 | Cook比較Probe | 固定Convex Dataset、U1 Unity BakeMesh Harness、N1／N2／N3 Native PhysX Harness、工程別Timer、Run Manifest、結果レポート | 同一入力でUnity経路の実費用とNative改善上限をP50／P95／P99まで再現測定でき、差の原因と版・設定差を区別して記録できる。Native PhysXを製品Runtime依存にはしない |
| Phase 0.5 | XRスモークテスト | OpenXR、Quest 3S有線Link、Grip Pose、Tracking State、GripToKatanaOffset、Single Pass | 空シーンで両眼90Hzと左右の刀姿勢・追跡復帰を確認 |
| Phase 1 | 即時切断 | 共通切断入力、単一clip、分離オフセット、簡易断面、ヒット演出、Micro Attachment即時消去、実Geometry Shard／Vertex Pulling／Indirect Batch PoC、VFX Graph汎用Fallback | 非VR入力で箱と代表プロップに即時の隙間を表示し、切断帯内の微小付属物が同フレームに消えて実形状のShardへ遷移する。通常数千Triangleを少数Drawで処理し、予算超過時は汎用破片へ安全に低下する |
| Phase 2 | 仮断面・影強化 | Cut Shell、OBB交差Cap Bounds Polygon、両眼Frustum／Facing Cull、Front／Back相殺とResidual Stencil Support検証、CapCompatibilityKey／互換Group、可視Cap Bounds競合判定、Winding Count Stencil、左右眼Stencil Conflict Graph／Greedy Coloring、Color単位Volume／Cap Batch、共通トゥーンの粘土色グレー、処理経路デバッグ色、ShadowCaster用per-instance clip／Offset、Stable片面／Pending両面Batch、XR両眼対応、Pending Cut管理 | 2～4連続切断と複数対象の画面重複でStencilが混入せず、OBBが重なってもCap非交差なら安全にBatchされ、互換Groupは統合され、両眼不可視Cap Groupは欠落や点滅なく除外される。相殺不能入力はFallbackし、Shadow MapではStencil Capなしの影近似が許容範囲に収まる |
| Phase 3 | 表示ジオメトリ | Job＋Burst三角形切断、Count／Write Job、ReadOnly／Writable MeshData、断面生成、メインスレッドMesh公開、世代Commit | 仮表示から実Meshへ無停止で置換し、重い頂点処理がMain Threadへ戻らない |
| Phase 4 | 物理 | 全体0.5G仮設定、FragmentGroup、PendingPhysicsSplit、Native Convex B-rep、Count／Write／Validation Job、Job化`Physics.BakeMesh`、Fast Cook初回分裂、選択的Fast Simulation再Bake、別Mesh差し替え、Upgrade Scheduler、質量特性、速度継承、Generation Reject、Timeout品質低下、予算管理、T-070との差分再確認 | cook遅延中も即時表示を維持し、Convex分割／BakeでMain Threadを停止させず、Fast Cookで早期分裂した後に価値のある破片だけを安全に昇格し、二重Meshメモリ、差し替え時の跳ね、Worker占有を許容範囲へ抑制する。Unity経路が要件を満たす限り維持し、満たさない場合だけD-086のGateを評価する |
| Phase 4.5 | 飛翔斬撃と未来評価 | Gesture状態機械、Edge Direction Gate、Recovery、NonCutting素通り、Slash Latch、Span／Travel Axis、単調・一価SlashFront、逆行／自己交差Finalized、前縁VFX、帯状Sweep、Candidate Flight Bounds、評価DAG、先行切断、Commit検証 | 復路とU字軌道で二重前縁や誤斬撃を作らず、Latch直後から三日月前縁が飛翔・命中し、Extending中も前縁が成長しながら進み、遠距離対象の多くが接触時に完成Meshへ即移行 |
| Phase 4.6 | 予測拡張 | 局所PhysicsScene、未来Animation姿勢、信頼度別フォールバック | 動的対象でも予測採用率と予測費用が基準を満たす |
| Phase 4.7 | モブ未来計画 | Mob Future Planner、MobPlan／PlanGeneration、AI LOD、経路・Animation先行確定、時空間予約、Trace | 介入なしの遠距離モブで計画再利用率と先行切断完了率が基準を満たし、介入時は安全に無効化される |
| Phase 4.8 | OpenXR Projection Capture | Windows API Layer、D3D11固定、SDR、MSAAなし、Dynamic Resolutionなし、Single Pass、Projection 1枚、左眼45fps、Release前GPU Copy、固定Profile検証、GPU Encode、Capture Record／Run Manifest同期 | 切断PoCの異常をProjection画像とTraceで再現調査でき、想定外構成はFail Fastし、非録画時との差が性能予算内。不要なら導入を見送れる |
| Phase 5 | 人形 | 姿勢スナップショット、CPUスキニング、骨proxy分類、物理移行 | 基本動作中のNPCを任意方向に切断 |
| Phase 5.5 | Asset自動前処理 | Portable Blender Manifest／Bootstrap、固定版ヘッドレス実行、開放Mesh修復、Voxel／SDF内部充填、Trusted Exterior分類、制約付きSurface Projection、Projection後自己交差検証、Reduction、Micro Attachment連結成分抽出／Recipe分類、AttachmentId／Anchor生成、Solid／Proxy生成、検証、キャッシュ | 古いシステム版と共存し、代表家具・車・建物を別PCでもGUIなしで再現生成する。主要外形をVoxel結果より改善し、自己交差入力をStable Solidへ通さず、重要部品を除外しながら微小付属物を安定分類できる |
| Phase 6 | コンテンツ | Synty City街区、10プロップ、シェーダ統一、既製モーション | 垂直スライスとして一連の遊びが成立 |
| Phase 7 | 最適化 | 端末別品質、破片LOD、ジョブ優先度、遠距離確定、ストレス試験 | ターゲット実機で性能予算を満たす |

## 16. 垂直スライス受け入れ基準

- 刀の高速移動でも代表プロップを安定して切断できる。

- 斬撃直後に切断された両側が離れて見え、仮断面が両眼で一致する。

- 通常断面は全体と同じトゥーン陰影の粘土色グレーで統一され、仮断面から実断面への差し替えで特殊な質感変化が見えない。

- 即時切断物体のShadowはカラー表示と同じclip／分離Offsetに追従し、両面Shadow近似からStable実断面の片面Shadowへ移る際に目立つ影の跳びがない。

- 左右眼の一方だけで非互換な可視Cap Boundsが重なる複数の即時切断対象はStencil Conflict Graphが別Colorへ分け、OBB投影が重なっても両眼の可視Cap Boundsが非交差なら同一Colorへまとめられ、別物体のStencilによる仮断面のはみ出しがない。

- 同じ全切断面とキャップ状態を共有する対象は重なっても同じStencil Colorへ統合され、別々に動いてWorld Planeが変わったフレームでは自動的に別Groupへ分かれる。

- 両眼とも裏向きのCap GroupはStencil処理ごと省略され、片眼だけ可視または切断面近傍では省略されず、頭部微動で仮断面が点滅しない。

- デバッグモードでは赤＝即時仮断面、青＝先行Commit、緑＝命中後計算CommitをTraceと一致して識別でき、Stable後は通常グレーへ戻せる。

- バックグラウンド完成後、表示MeshとColliderが目立つポップや停止なく差し替わる。

- 表示Meshと物理Convexの切断、検証、cookingはJob＋Burst主体で実行され、Main ThreadにはMesh公開とRenderer／Collider／Rigidbodyの境界Commitだけが残り、未完了Jobへの強制`Complete`によるフレーム停止がない。

- Unity `Physics.BakeMesh`とNative PhysX比較Probeの入力、版、設定、工程別結果が再現可能に保存され、倍率差だけを理由にNative Backendが製品へ混入しない。Native再検討時はD-086のGateを満たした証拠を残す。

- 処理中に再切断しても、古いジョブ結果で形状が巻き戻らない。

- NPCを移動中に切断し、姿勢固定から剛体破片への移行が成立する。

- 代表的な連続切断シナリオで目標フレームレートとメモリ予算を満たす。

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

> **次の推奨アクション** Phase 0として非VR固定テストと共通切断入力に加え、ProfilerMarker、Flow Event、固定長TraceLogger、最小Editorタイムライン、FrameId付きの選択的静止画／片眼録画を先に用意する。箱とSynty代表プロップ1個の単一切断で性能基準、完全なWork Item／Job時系列、対応画像を取得する。続いてPhase 0.25のCook比較Probeを測定専用で実施し、Native依存を製品へ持ち込まず結果を固定する。その後Phase 0.5でQuest 3S有線Linkの両眼表示と90Hzを確認する。OpenXR API Layerは切断PoC成立とT-054完了後まで実装しない。

## 18. 用語

| 用語 | 定義 |
| --- | --- |
| Stable Geometry | バックグラウンド処理が完了し、表示・物理へ確定適用された形状 |
| Pending Cut | シェーダでは見えているが、実MeshまたはColliderへ未反映の切断 |
| Solid Cut Mesh | Blenderプリプロセスで入力Assetから生成する、表示には使わないTopological Watertightかつ自己交差のないGeometrically Validな基底形状。初回の内部判定、断面生成、反復切断の入力となる |
| Cut Shell | 基底Solid Cut Meshまたは直前のStable Cut Shellへ確定済み切断を適用して派生する、現在のObjectGenerationを表す閉じた実行時形状。Stencil内部判定と次回切断に使う |
| Physics Proxy | 物理接触と高速切断のための低複雑度Convex／Compound |
| FragmentGroup | 物理分裂Commitまで、複数の表示・論理破片を1つのRigidbodyと旧Colliderで支持する一時的な物理単位 |
| PendingPhysicsSplit | 見た目と論理状態は切断済みだが、左右のBake済みColliderが未完成でFragmentGroupの物理モデルを共有している状態 |
| Cooking Profile | `Physics.BakeMesh`と`MeshCollider`へ同一指定するcookingOptionsの構成。初回分裂用Fast Cookと選択的Upgrade用Fast Simulationを使い分ける |
| Physics Upgrade | Stable Fast Cook破片と同じ形状の別MeshをFast Simulationで再Bakeし、安全な物理ステップ境界でColliderを昇格させる処理 |
| Micro Attachment | Physics Proxyで表現しない微小な付属部品。切断帯へ触れた場合は物理破片を作らず不可逆に全体消去する |
| Attachment AliveMask | AttachmentIdごとの生存状態。即時表示、確定Mesh、再切断、世代管理で共有し、消去済み部品の再出現を防ぐ |
| GPU Micro Debris | Micro Attachmentの実GeometryをShard Cluster化し、Vertex Pulling、解析運動、Indirect Batch、Opaque Dither Clipで描く短寿命・衝突なしEffect。汎用ローポリ破片はFallback |
| Debris Geometry Atlas | Micro AttachmentのVertex、Corner／Index、Shard Metadataを事前登録し、発生時のGeometry転送とBuffer再確保を避ける共有GPU Buffer群 |
| Shard Cluster | 接続、Normal、Material、面積を基準に隣接する通常2～8 Triangleをまとめ、同じGPU Transformで飛散させる単位 |
| WorldPhysicsProfile | 世界重力を正本として保持し、Unity Physics、予測、解析運動、GPU Effectへ同じ値を供給するバージョン付き設定 |
| Pending Two-Sided Shadow | 即時切断中だけ、開いた外殻の裏面をShadow Mapへ書いて断面キャップの遮蔽を近似する両面ShadowCaster経路 |
| Cap Bounds Polygon | 対象のローカルOBBと切断平面の交差から生成し、他のPending Cut半空間でclipする3～6頂点の有限な仮キャップ板 |
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
| Unity Built-in 3D Physics | GameObject／Rigidbody系で使用するUnity内蔵NVIDIA PhysX統合。DOTSの`Unity Physics`パッケージとは別物 |
| Native Cook Probe | Unity `Physics.BakeMesh`と、別HarnessのNative PhysXによる頂点Hull／完全Topology／直接生成を同一Datasetで比較する測定専用実験。製品Backendではない |
| Native採用Gate | Unity経路の実要件違反、Unity側最適化の枯渇、大きな継続差、実ゲーム統合Prototype成立をすべて要求する部分置換の判断条件 |
| Prediction Physics | 独立PhysicsSceneで局所物理島を未来へ進め、命中予定姿勢を求める処理 |
| Confidence | 未来結果をDeterministic／Conditional／Speculativeに分類した信頼度 |
| Trace Event | 状態遷移、Taskライフサイクル、Commit結果を整数IDと時刻で表す軽量イベント |
| Flow Event | Schedule元と別スレッド／Job上の実行をUnity Profiler内で結ぶ相関情報 |
| Flight Recorder | 直近イベントを循環保持し、異常検出時に前後履歴を固定・保存する仕組み |
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
Timestamp / Frame / FixedStep / ThreadId
SlashId / SlashGeneration / FrontEdgeId / ObjectId / ObjectGeneration
MobId / PlanGeneration / TaskId
CaptureFrameId / OpenXRFrameId / TestRunId
EventType / TaskType / FromState / ToState / Reason
Value0 / Value1
```

最低限記録するイベントは、`BladeTrackingLost`、`BladeTrackingRestored`、`BladeSamplesReset`、`EdgeGateEntered`、`EdgeGateRejected`、`SlashPrimed`、`SlashLatched`、`SlashFrontCreated`、`FrontVertexAdded`、`FrontEdgeActivated`、`FrontSampleIgnored`、`FrontTopologyRejected`、`SlashFinalizedByReversal`、`SlashFinalized`、`SlashFrontExpired`、`SlashRecoveryStarted`、`SlashRearmed`、`FrontHitConfirmed`、`CandidateDetected`、`TaskScheduled`、`TaskStarted`、`TaskCompleted`、`PredictionValidated`、`PredictionRejected`、`GenerationChanged`、`MobPlanCreated`、`MobPlanExtended`、`MobTierChanged`、`ReservationCreated`、`MobPlanInvalidated`、`MobReplanned`、`MobPredictionUsed`、`MobPredictionRejected`、`CaptureFrameQueued`、`CaptureFrameEncoded`、`CaptureFrameDropped`、`CaptureRingFrozen`、`ProjectionCaptureCopied`、`CommitStarted`、`CommitSucceeded`、`CommitRejected`、`FallbackActivated`、`TaskCancelled`、`ResultDisposed`とする。既存Event名の`Task`は論理Work Itemを指し、`TaskCancelled`は原則としてSchedule前の取消または取消可能なI/O処理にだけ使用する。Schedule済みJobの不採用は`PredictionRejected`／`CommitRejected`と`ResultDisposed`で表す。

Jobからは`NativeQueue<TraceEvent>.ParallelWriter`等のBurst互換経路へ書き込み、メインスレッドがフレーム末尾に回収する。毎フレーム全状態をスナップショットせず、状態遷移と重要な判断だけを記録する。

### 21.4 固定長バッファと異常時保存

初期値として直近30秒相当を固定長リングバッファで保持する。容量超過時は古い正常イベントを上書きし、記録処理を停止させない。

不変条件違反を検出した場合はバッファを保護し、可能なら追加で約5秒記録してから保存する。保存対象にはTrace本体のほか、ビルド識別子、シーン、乱数Seed、固定時間刻み、品質設定、対象世代、斬撃入力を含める。

自動保存トリガーは、二重Commit、存在しないTaskの完了、Slash／Object／Plan Generation不一致Commitの試行、Hit未確認Commit、Pending状態のタイムアウト、表示破片とColliderの不一致、成果物の未解放を基本とする。

### 21.5 Editor Timeline

最初は独立したEditorWindowとして実装し、Unity ProfilerのカスタムModule化は必要性が確認されてから行う。

- 横軸は時刻またはフレームとする。
- レーンはSlash、Object、MobPlan、Task、Threadを切り替える。
- `SlashId`、`ObjectId`、`ObjectGeneration`、`MobId`、`PlanGeneration`、`TaskId`、失敗理由で絞り込む。
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
CaptureProfileId / RunManifestHash
```

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
