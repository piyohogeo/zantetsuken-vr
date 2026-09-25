# D6内部分類lease／未移譲入力rearm

製品基点`54515f9e6fc8808eab1ac73f52c5659597074717`。D6専用入口の前段componentだけを移植する。通常Gameplay、cold factory、D3→Registry→DAG登録adapter、旧hit bridgeはまだ接続しない。

## 実装

- `PreparedCutLease`はDriver内のinternal型。未移譲の`VpPreparedPhysicsInput`から分類し、shapeのwork holdを取得する。Driver・Bind世代・Driverのframe source・shape・plane・massの一致と一回消費を要求する。private試作のpublic receipt／診断カウンタは移植しない。
- lease Disposeは未消費の分類だけを解放し、shape holdは一度だけ返す。Driver共通coreが消費後の分類を所有し、transactionへ移譲する前の拒否・例外はfinallyで解放する。transaction登録後の例外では分類を二重解放しない。
- 通常`RequestCut`と内部`RequestPreparedCut`は同じcoreへ入る。通常の権限・latch・owner検査、admission、公開、Final、recut、Abortは維持。prepared経路は受け取った分類を使い二重走査しない。内部Fresh評価はEmptySide優先、Fullは予約ではない。
- internal rearmは成功pose済み・未移譲・未Dispose・Mesh holdが自身のみ・bank／work読者0の場合だけ許可する。Mesh内容もB-rep領域も作り直さず、次のTryPoseでbind点から同じ領域へ出力する。不正pose・移譲済み入力を復帰させない。
- `everAttempted`を分け、rearmした入力をD5用の元のcold入力と誤認しない。public TryPoseは引き続き一回用であり、再準備capabilityはpublic APIにしない。

生の`TryClassify`を外部で呼んだ読者はこのleaseでは追跡されない。次工程の専用factory／handleがD2を内部所有し、生shape／分類を外へ渡さないことが前提。今回は任意の既存shapeを安全にmutable化したという主張ではない。

## 検証条件

licensed素材を使わず、既存の合成box harnessとDriverのguarded teardownを使う新規20条件。分類同一参照の一回移譲、Driver／frame／rebind／plane／mass／shape不一致、Dispose／null、EmptySide優先／Full、入力Dispose中のlease保持、公開後例外、3回rearmのMesh／bank不変・pose／bounds更新、work／bank view／cold Mesh hold、不正pose／移譲後／Dispose後を確認する。新規testは既存fixtureのpartial拡張であり、通常Driver testを差し替えない。

Unityは製品プロジェクトを直接開かず、`Prepare.ps1`で製品候補をprivate内の`Working/D6L-54515f9/Project`へコピーする。以前のprivate runtime overlayやInertial motionは加えない。既存回帰用のScenes、製品に導入済みのLicensed intake、Tools/Phase02合成fixture、privateのPhase0.2/Adoptedを`Complete-Fixtures.ps1`で追加する。最後のdatasetは旧testが相対siblingを探すため、private Working内の専用siblingへコピーする。private全体への循環junctionは作らない。ライセンス素材はprivate検証コピーから外へ持ち出さず、製品commitへ追加しない。

`Run.ps1`は起動前に全Runtime／Testsを製品候補とbyte比較し、変更7 C# sourceのsnapshotとXML／logを製品`Logs/PreparedClassificationMigration/<run>`へ保存する。`Inspect.py --check`は候補一致・fixtureのsourceとのbyte一致・新規20条件・全EditMode回帰と保存summaryを検証する。失敗runは消さず残す。

## 実行記録

- `focused-1`：新規合成20/20通過、skip／inconclusive 0、PID 132760。
- `full-1`：3,733/3,841通過、25失敗・83skip。初版PrepareがScenes・Tools/Phase02・Licensed入力をコピーしていなかった。25失敗はadopted index未発見4件、Sandbox scene未発見21件。83skipもprivate fixture未導入であり、回帰合格として扱わない。このrunではライセンス素材を使用していない。実装ソース／test／期待値を変えず、欠けた入力を追加して再実行した。
- `full-2`：**3,841/3,841通過、fail／skip／inconclusive 0**、PID 124544。既存3,821＋新規20条件。全Runtime／Tests 1,405ファイルは製品候補とbyte一致し、候補側の余分なファイルもない。4群のfixture（Scenes、Licensed intake、public adopted、licensed adopted）は検証後に元とbyte一致を照合した。新規D6 test自体は合成入力のままであり、実Inertial characterへの新D6入口接続を確認したものではない。

Unity 6000.3.22f1、非表示batchmode EditMode。検証コピーでのインポート／試験に伴う設定変更は製品へ戻さない。`summary.json`へ全runのソース・XML・logのSHA、失敗／skip理由、現在候補とfixtureのmanifest digestを固定する。再照合は`python docs/diagnostics/compact16uv-prepared-classification/Inspect.py --check`。性能測定は行っていない。

## 残工程

D6 cold factory／専用handle、Busy再入と通知中Dispose、部分登録後異常の分類、実characterでの再統合、関連PlayMode／Player、性能は別工程。内部component移植だけなので通常Gameplayの同期入力方式を移行済みとするDESIGN更新は行わない。

旧privateのD6証拠と旧D5 verifierは旧製品基点／旧sourceに固定した記録。今回の新source／新commitを旧試験へ当てて「再検証済み」とはせず、旧ファイルはそのまま保持する。次の実asset検証は新commit基点の別版にする。
