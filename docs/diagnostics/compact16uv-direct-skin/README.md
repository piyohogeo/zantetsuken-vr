# Direct16 current-pose component migration

2026-09-25。製品基点 `523f6249bfbdc49a36702f832777622891434fce`。
privateで検証したB／V12のうち、**D1（同期自前skin＋CPU Storage直接出力）だけ**を移植した。
通常Gameplay、初回body登録、cook、hit移譲、Provisional公開はまだ切り替えていない。
ライセンス素材、privateの環境変数／時計／oracle／fixture、inactive pairは追加していない。

## APIと費用の位置

- `VpDirectSkinInput.TryCreate(renderer, topology, topologyCount, out input)` は読み込み時の準備。未対応／不正入力ならfalse。Renderer表示やStorageを変更しない。
- `input.TryAppendTo(storage, out geometry)` はMain上の同期処理。現在bone行列収集→Storage予約→Burst direct callのskin／oct encode／bounds／topology出力→global index補正コピー→公開。通常成功時のBakeMesh、一時出力Mesh、全頂点再検査、後段bounds走査、32B→16B再packを行わない。
- 結果はCPU Storage内部のPublished Geometryであり、GPU転送やGameplay公開ではない。body登録や表示採用は従来どおり別境界で行う。

対応範囲は16B、CPU-readable、blend shapeなし、単一triangle submesh、全頂点参照、1–4 influence、4-weight Quality、正規化finite weight、閉じたauthoring topology、rigid/unit-scale Renderer frame。
固定import scaleは既存`VpFixedScaleSkinInput`で準備する。Renderer-localの基底・bone順序・bindposeを混用しない。
source属性／weight／index／topologyのgateとUV量子化はcold。canonical topologyを共有するseamは同じbind位置と同じweight演算順であることをcoldで確認する。位置weldはしない。

Rendererのframe／Quality／Mesh identityと使用boneの現在行列finite性はhotの必要な動的条件。
出力position／normalのfinite性とnormalの非zero条件はskin loopに融合し、失敗出力を公開しない。
oct encodeはrsqrtなしのStrict除算版。rcpや新しいwarm案の追加比較は本移植へ混ぜない。

## 所有権と失敗

外部へ予約token、書込みpointer、`generatedValid`フラグを渡す汎用APIは追加しなかった。
Storage内部の同期scopeが全spanとindex予約を取得し、失敗時はfinallyで返す。正常公開後は取消しない。
APIから二重publish／wrong-storage予約消費を表現できない形にしている。結果Geometryを別Storageへ渡すケースと、IB再利用後の古い世代拒否はテストする。

既存cut出力予約が開いている間は従来appendと同じくfalse。予約を横取りしたり強制Completeしない。
このD1は非同期Work予約APIではない。同期scope内にcallback／await／Unity object操作はなく、他要求への共有scratch／予約持越しもない。新しい予約classのhot割当は追加していない。

入力Native配列はproducerが所有し、rig／source Meshは借用する。`Dispose`は二重呼出し可能で公開済みGeometryには影響しない。
出力はStorageのcopyなので後続poseや入力解放で書き換わらない。
**Mesh内容・bone binding・topologyを変更する呼出側は、変更前にinputをDisposeし、coldで再作成する。** 毎hitのsource hash走査やbone配列の再取得はしない。Mesh identity変更はhotでも拒否する。
未対応入力の自動fallback呼出しはこのcomponentには含めず、今後のadapterで既存同期経路へ接続する。

## 検証

新規合成入力テストは **33/33通過**（focused-5、skip 0）。元Unity `BakeMesh(true)`との比較は基準pose／4骨の移動回転＋Renderer移動回転／骨の非一様scaleの3条件。
全頂点位置差3e-6未満、oct復号normal角度差1.4°未満をtest条件とし、UV中心・index・topology・boundsも確認した。この許容値はtest条件であり、実行時の全頂点比較や製品の一般的誤差保証ではない。

非zero offset、反復poseの出力不変、別Storage、容量不足5種、invalid normal出力後の全予約返却、cut予約との排他、input／Storage dispose、動的frame／quality／source変更、cold不正入力、5 influence拒否、seamのweight不一致拒否、caller topologyのcopy、input再作成、IB退役後の古い世代拒否も確認した。

全EditMode回帰も **3,737/3,737通過**（既存3,704＋新規33、full-1、skip 0）。最終件数・全6 runのXML／log／source SHAは[summary.json](summary.json)に記録した。

### 途中の失敗も保持

| run | 結果 | 分類／修正 |
| --- | --- | --- |
| focused-1 | 0件実行、exit 0 | 新規test metaのGUIDが33文字。32文字へ訂正。合格には数えない |
| focused-2 | 28/29 | unused頂点fixtureのvertex数変更でUnityがnormal配列を消去。変更前に属性を保存して構築し直した |
| focused-3 | compile停止 | 追加5 influence testのusing NativeArrayへの書込み。managed配列から構築する形に修正 |
| focused-4 | 32/33 | 旧BoneWeight channelからvariable weightへのUnity変換error。新規Meshで5 influence入力を直接構築 |
| focused-5 | 33/33 | 修正後の全合成入力テスト通過 |
| full-1 | 3,737/3,737 | 既存全EditMode＋新規33件の回帰通過 |

全runを`Logs/Direct16Migration/`に別名保存し、失敗runを上書きしていない。Unity起動前に同一worktreeのEditor／import worker競合を確認し、非表示batchmodeで実行した。これは機能テストで、他projectとの性能測定用隔離やIL2CPP測定は行っていない。

テスト起動でUnityが自動更新したOpenXR設定は、起動前にcleanだった当該ファイルだけを元へ戻し、差分0を確認した。新規runtime／testの8ファイルSHAはsummaryと一致。途中runを含むlicense handshakeメッセージは残るが、最終2 runはテスト完了・exit 0を確認しており、ライセンス設定の変更や回避処理は追加していない。

## 残る範囲

Editor componentの合格であり、Player/Burst AOT、実キャラクターasset／実clip、物理shape、初回登録＋同frame Provisional、画像／VR、Main短縮率の合格ではない。
現在poseの製品既定経路はまだBakeMesh。privateの性能値を製品達成値として転記しない。

次はD2の個体別prepared shape・cold cookと、Provisional／FinalのMesh-to-owner配置・解放を移植する。共有cook Meshやinactive pairは同時に導入しない。ライセンス付き比較はprivate fixtureで行う。
