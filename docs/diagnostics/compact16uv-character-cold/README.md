# D6専用handleのcold資源所有（前半）

2026-09-26。製品基点`2ae6c9f26886559b7025b0bc0e78367a8ccc7318`。前段の内部分類lease／rearmに続き、**cold準備・Ready・Disposeだけ**を製品へ移植する。同期TryCut、source actor／旧hit bridge、D3→Registry→DAG adapter、Busy再入／通知中Disposeはまだ実装しない。任意の登録途中失敗をcold資源のDisposeで巻き戻せるという意味ではない。

## APIと所有権

`CutWorldRoot.TryPrepareCharacterCut`へ正規化済みSkinnedMeshRenderer、render topology、検証済みbone-local B-rep bank／ranges、convexごとのTransform、bootstrap共有`VpPhysicsColdPreparation`をcoldで渡す。素材探索・JSON parse・骨名解決はauthoring/intake側で先に済ませる。この段階では一般素材loaderも旧hit対象の列挙も移植しない。D1の既存cold検証（unit scale・4-weight・単一submesh・非blendshape等）を使い、未対応はfalseとする。

返る`VpPreparedCharacterCut`がD1 producer、D2 cook済み入力、D3表示準備枠、convex→Transform配列のコピーとpose行列配列を単独所有する。callerのmutable D2入力を借りず、生shape／分類／rearm／slotを返さない。D2は元のB-repをコピーするため、factory成功後に元bankを解放できる。Rig・Mesh・Transform・World・共有D5はborrowedで、handleは破棄しない。Mesh内容・bone binding・authoringを変更する場合は作り直す契約を維持する。

順序は、入力対応検査→D1作成→D3枠作成→D2 Mesh作成／cook→共有D5呼出し。D3がcoldで拒否した場合はMesh/cookをまだ作らない。途中のfalse／例外はfinallyで未公開資源を解放し、完成したhandleだけを返す。D2 ctorの途中失敗でも、既に作ったMeshと先行したD3枠を残さない。意図的な例外注入は不正な2番目のconvexに限定し、全native生成箇所・OOMを網羅したとはしない。

準備中はStorage append、Ledger fragment／operation、Registry登録、GPU転送、Provisional公開を行わない。D4の総容量準備・実要求のadmission・storage容量確保の代わりにはならない。

## PlayModeのReadyと解放

factoryのtrueはhandleを作れたという意味で、同frameのReadyは保証しない。D5のinactive一時root／Colliderの遅延Destroyが残る間は`IsReady == false`。後続のロードframeで`TryFinishPreparation()`を呼び、共有D5がMesh holdを返せた後だけReadyとなる。待機・spin・hit内warmは追加しない。`IsReady`はcold準備資源の寿命条件であり、現在poseの健全性や受理予約の保証ではない。

準備待ち中でもhandleはDispose可能。D2のMeshは一時Colliderの独立holdによって保たれる。**Dispose済みhandleは共有D5をfinish／Disposeしない**ので、bootstrapはhandleを捨てた場合もD5を保持し、後続ロードframeで`TryFinish()`を終える責務がある。World終了もhandleのD1／D2を自動破棄しない。callerが必ずhandleをDisposeする。Worldが先にdisplayを破棄した場合、D3枠の二重Disposeは安全。

## 検証

新規EditMode14条件：cold時の未登録／未転送、入力コピーと元bank解放、二重Disposeとborrowed rig維持、2 handleの資源独立とD5共有、10種の未対応／不正入力拒否、部分cook失敗、World先行終了。新規PlayMode2条件：2 handleでもD5一時rootは2個のみ、ロードframe後のReady、待機中二重Dispose、独立holdによるMesh寿命、borrowed rig維持。

新規testは合成tetra render／box convexだけ。旧mass activation fixtureのPlayMode helperをpartialで再利用するが、既存testの内容は変えない。追加UnityTearDownは新cold資源を作っていなければ即returnする。実assetの新入口・同frame切断・Final／recut／worker寿命の再検証を完了したものではない。

`Prepare.ps1`は製品候補・Scenes・導入済みLicensed・public fixture・private adopted fixtureを、private内の新しい`Working/D6C-2ae6c9f`へコピーする。製品Unityと旧検証環境は開かず、旧private runtime overlayを使わない。`Run.ps1 -RunName focused-1`、`full-1`、`play-1`、`related-1`は別プロセスの記録を保存する。rawは製品`Logs/CharacterColdMigration`、summaryへsource／XML／log SHAを固定する。fixture payloadはprivateのみ、commitには追加しない。

`Inspect.py --check`は全Runtime／Testsの候補一致（余分なファイルも拒否）、現在sourceと一致する成功run、既存fixtureの元との一致、保存summaryを照合する。旧D6 leaseやD5のverifierは旧source基点のまま残し、新HEADへ書き換えない。性能・通常Gameplay・Player／IL2CPPは未検証。次はこのhandleの内部で同期TryCutとBusy／移譲／使用不可状態、D3専用登録adapter、旧hit退出を接続する。

## 実行結果

Unity 6000.3.22f1、非表示batchmode。今回の候補はソース修正・閾値変更なしで全runが初回通過した。

| Run | 結果 | PID |
| --- | --- | --- |
| focused-1（新規EditMode） | 14/14 | 18516 |
| play-1（新規PlayMode） | 2/2 | 80236 |
| full-1（全EditMode） | 3,855/3,855 | 57668 |
| related-1（D5・mass activationを含むPlayMode fixture） | 7/7 | 49832 |

全runでfail／skip／inconclusive 0。新規14件は全EditModeにも、新規2件は関連PlayMode7件にも含まれ、独立条件として加算しない。Runtime／Testsの全1,411ファイルが製品候補とbyte一致し、fixture 4群も元と一致。source snapshotは変更した5 C#ファイル。実行中の製品worktree変更はこの実装・test・診断文書のみで、Unity生成設定を戻す操作は行っていない。性能・Main費用・native memory peakをこの試験から推定しない。
