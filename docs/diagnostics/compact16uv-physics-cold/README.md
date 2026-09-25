# Fresh joint／初回physics準備（D5）

2026-09-25。製品基点`64fc03d7fe8eaadc264cf23206530ceb5dd734c8`。B／V12のfresh joint既定setter省略と使い捨てcold呼出しを限定移植した。privateのON/OFF flag・時計・JSON・可変Mesh frame辞書・inactive Actor保持poolは取り込まない。

## 常用経路の構造的省略

`ProvisionalSeparation.Configure`は毎回`AddComponent<ConfigurableJoint>`する。そのfresh componentでUnity 6000.3.22f1の既定値と等しい5 setter（anchor=zero、projection=None、breakForce／breakTorque=+Infinity、enableCollision=false）を省略する。残る12 setterと軸計算・接続body・auto anchor無効化・1 m limit／offsetは維持する。既存の通常Provisional構築にも適用されるが、pooled／deserialized jointの再設定には使えない。今後Configureを再利用joint向けへ変えるなら省略したsetterを戻す。

test側に移植前17 setterのbaselineを固定し、±XYZと2斜軸の8方向×inactive／activeの16条件で設定値を比較する。native defaultの5項目、body対応、motion／anchor／limit、各driveも確認する。Editorのactive化はsolver挙動の証拠ではなく、接触品質・制約による移動保証は追加しない。

## 明示cold入口

ロード時にapplication/bootstrap所有の`VpPhysicsColdPreparation`を1つ作り、pose出力前の代表`VpPreparedPhysicsInput`を`Prepare(input)`へ渡す。成功後は同じpreparerの呼出しがno-opになる。**プロセス共通の静的global flagではなく、callerが共有するbootstrapオブジェクト単位**。NPCごとに作り直して重複warmしない。通常Gameplayへの自動呼出しはまだ追加しない。

実行するのは次の処理だけ。

1. D2入力から全convexを参照する一時Provisional shape viewを作って解放する。
2. 代表shapeのbounds中心を通るY平面で分類して解放し、軸平面・斜平面のBoxMass計算を各1回実行する。
3. inactive化後にRigidbody付きrootを2個作り、実`PhysicsOwnerSide`を通して`List<MeshCollider>(4)`を準備する。同じside構築がListを通るため、独立した重複List warmは追加しない。
4. fresh jointを実Configure経路で作り、代表cook済みMeshに対して専用convex frame子＋MeshColliderを1個作る。cook profileを揃え、Mesh内容やposeは変更しない。追加の明示`Physics.BakeMesh`呼出しはない。
5. 両rootを破棄要求する。EditModeは即時、PlayModeは既存製品規則と同じframe末のDestroy。戻り時点でinactiveなので公開・query・simulationには参加しないが、PlayModeで返った瞬間にnative objectが消滅したとはしない。Mesh sourceに独立holdを取り、ロード中の後続frameで`TryFinish()`が両rootのUnity-nullを確認してからholdを返す。

`IsPrepared`はwarm処理成功かつMesh hold解放済みを表す。PlayModeで`Prepare`直後はfalseとなり、**preparerを保持して後続ロードframeで`TryFinish()`がtrueになるまで確認する**。spinやhit時の完了待ちはしない。pending中のDisposeは例外で拒否し、holdを早期解放しない。native破棄を待つ間は両rootへの参照とMesh sourceを一時保持し、完了後はフラグだけになる。bank／shape／poseは保持しないため、入力のpose出力・移譲・Disposeを待たせない。holdの最後のReleaseがMeshの遅延Destroyを要求する場合、そのMeshのnative消滅はさらに後であり、IsPreparedはnative allocatorが全回収済みという意味ではない。

Registry／Ledger／ID／admission／Storage／GPUへ接続せず、実ActorやColliderの再利用poolを残さない。代表入力はunposed・未移譲・worker／bank borrowerなしを要求し、pose済み・失敗pose試行済み・移譲済み・Dispose済みは初回準備を拒否する。warm成功後のPrepareは引数を再検査せずTryFinishだけを行う。途中例外時も一時view／classificationを解放し、native rootを破棄要求するが、pendingならpreparerを保持して同じTryFinishでMesh holdを返す。完了前の失敗を自動retryせず、engine内部の初回cache効果も巻き戻さない。callerがpreparerを捨てればholdを漏らすため、完了確認はbootstrapの責務。inactive GameObjectのOnDestroy通知には依存しない。

分類やmassの一時結果を実要求へ使い回さない。要求時の現在pose・分類・mass・実Collider生成・公開は依然必要。代表shapeで一度通したことから、任意shape・別thread・全native内部経路がwarm済みとは保証しない。代表shapeにbounds／分類／BoxMass成立が必要であり、失敗時にshapeの許容を広げたりpose attemptを消費して回避しない。

## 検証の範囲

修正版の全EditMode `full-1`は**3,821/3,821通過**（既存3,797＋D5新規24、skip／inconclusive 0）。新規PlayMode `playmode-2`は1/1、関連PlayMode `provisional-playmode-1`のPhysicsCut名前空間13件も通過し、新規の遅延解放testを含む。

ただし後者の広い`Provisional` filterはStandaloneRenderingの`PaletteAtlasPlayerTests.StencilProvisional_UsesRedSlot_SwitchesWithoutUpload_AndStillNeedsPositiveStencil`も拾い、同testがgrey／red画素数比較（期待2048、実際6144）で失敗したため、**run全体は13/14の失敗**である。製品のD5 APIを直接使うtestではなく、そのソースも変更していないが、D5と無関係と断定せず単独追試で切り分ける。物理13件の成立と描画testの失敗を分け、全PlayMode回帰合格とは書かない。

新規EditModeは24件：joint16条件、未初期化／0x00／0xCDの3状態でcold後のB-rep／Mesh／bounds／hold／追加cook数不変・native object回収・後続pose／pair構築、non-cold拒否4条件、null拒否と成功後のno-op。privateの検査counterを製品へ移さず、test側で照合する。

上記Atlas／Stencilは無変更の単独`palette-recheck-1`で1/1通過した。元のgrey／red不一致の原因は未確定であり、実行順・残存scene等を確認したとはしない。失敗を修復済み・全PlayMode成功へ読み替えず、runとソースSHAを両方保持する。

Unity起動前はcleanだったが、起動が`Assets/XR/Settings/OpenXR Package Settings.asset`を自動更新し、`ProjectSettings/SceneTemplateSettings.json`を新規生成した。両方を`Logs/PhysicsColdMigration/unity-generated-settings/`へSHA一致で退避した。OpenXR復元は一度安全審査で保留となったが、2026-09-25にユーザーの明示承認を得て**復元完了**。OpenXRは開始時のGit blob `3ecd4343a07b606469695cd872f833ee3fb67d40`との一致を確認し、開始時に存在しなかったSceneTemplateSettingsは削除した。生成版2ファイルのバックアップは保持し、復元直後の製品worktreeはclean。D5実装・テストソースは変更せず、Unity再起動・テスト再実行は行っていない。

新規PlayModeは、返却時の2 inactive rootと1 Collider、query非参加、同frame再呼出し、pending中のDispose拒否、cold後のpose出力とinput解放、次frameでroot／Collider消滅と独立Mesh hold維持、TryFinish後のMesh破棄を確認する。PlayModeテストはEditor上であり、standalone Player／IL2CPPの合格ではない。

初回`playmode-1`は0/1失敗として保持する。最初の実装は非アクティブColliderの`sharedMesh=null`設定で即時切離しできると仮定したが、読み戻しで元Meshが残った。原因をUnity全般の仕様として断定せず、この実測を根拠に切離し依存を撤去した。修正版`playmode-2`は1/1通過。閾値緩和や失敗の削除ではなく、破棄確認までMesh sourceを保持する所有権修正である。初回EditMode24/24成功版と初回PlayMode失敗版のソースは各raw `source/`へ固定する。

Unity 6000.3.22f1の非表示batchmode。raw証拠は`Logs/PhysicsColdMigration/`。source／XML／logのSHAを[summary.json](summary.json)へ固定し、`C:/Python38/python.exe docs/diagnostics/compact16uv-physics-cold/Inspect.py --check`で照合する。

## 未完了

速度、cold追加費用、厳密なnative allocation量・frame peak、Player／IL2CPP、実assetでD4＋D5を含めた再統合、通常Gameplayのcold入口接続、D6の実hit所有権境界は未完了。native生成途中の例外注入を全箇所行ったものでもない。旧B／V12の短縮率は今回の製品版の値に転用しない。旧private snapshot／数値とライセンス素材には触れない。

次はprivateの別検証版でD1～D5を実アセットへ接続し、coldから登録・Final・再切断・終了まで確認する。その後D6の同frame hit公開と拒否／Abort／staleを製品へ接続する。
