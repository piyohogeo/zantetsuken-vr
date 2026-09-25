# D6限定同期TryCut・登録・退出bridge

製品基点`cdb53ea0e47dbdd5f58a04eb396d7c50d27b040a`。cold handleへ、現在pose→分類→D1直接append→D3表示→Registry／DAG→Driverの同期入口を追加する。一般TryAddBody／登録済みRequestCutを置換しない。通常Gameplayへの自動配線・実hit検出・実asset／Player検証・性能測定は別工程。

## coldで結ぶ対象

`CutWorldRoot.TryPrepareCharacterCut`の追加overloadへ、以前の引数に加えて`characterRoot`と`motionBody`を渡す。characterRootはborrowed rig・旧hit物理・motion bodyを含む専用階層で、Worldを含んではならない。1 characterにつきlive handleは1つ、階層／binding変更・外部からの再活性化前には無効化するcaller契約。骨名検索・JSON読み込み・対象探索はcoldのcaller側で済ませる。既存coldだけのoverloadも残すが、source未結合のhandleではTryCutはUnavailable。

handleがcoldで作る専用source actorはColliderなし・gravityなし・collision detection無効のRigidbody 1個。未登録時はinactiveで保持し、hotでは現在Rendererの位置／回転、motionBodyのmass／COM／inertia／速度を写す。中間Collider、hot Mesh生成／cookは追加しない。Renderer-localとlineageは同一、unit scale、4-weight、単一submesh、初回anchorなしに限定する。借りたmotionBodyやrigをWorldへ所有権移譲しない。

## 同期要求

`handle.TryCut(plane, renderAnchor, positiveImpulse, negativeImpulse)`をMainの通常更新、物理step外から呼ぶ。planeは現在Renderer-local、renderAnchorはworld-space、impulseはcaller指定。戻り値の`Outcome == Requested`はDriverを呼んだという意味で、**公開成功はAcceptance == Publishedで判定**する。Source／Operationは戻り値とhandleから読める。例外時も既に発行したIDはhandleに残す。生分類／shape／slotは公開しない。

現在骨行列を準備済み配列へ集め、D2へposeとboundsを出力し分類1回。EmptySide優先、次にFull。どちらも分類leaseを解放して同じD2をrearmし、Storage・登録・GPU転送を増やさず旧characterを維持する。Fullは予約ではない。入力pose不成立はterminalとする。明らかに不正なplane／impulseはpose前にFailedで拒否し、その場合だけ準備を消費しない。

ReadyならD1が16B Storageへ直接skin＋boundsとindex offsetを書き、D3枠で表示登録、shapeと専用actorをRegistryへ移譲し、DAGを登録する。その後に同じ分類をDriver共通coreへ一回渡す。全頂点再検証、二重分類、bounds再走査、Mainの再pack、warm／待機／queue／一般経路fallbackは追加しない。D1に元からある品質・有限性・rigid検査は削除しない。

## 再入・退出・所有権

handleは呼出し中Busyとなる。同じWorldの別prepared handleからの再入も、World内の同期scopeで拒否する。正常拒否のEmptySide／Full以外は一回用。D1 append falseの理由を推定して再利用や一律Player終了を行わない。

Registry所有者にだけborrowed characterRootを結び、既存WithdrawがIsWithdrawnを確定してから階層をSetActive(false)する。戻り後ではなくProvisional公開中の旧owner退出に合わせて旧hit・表示・animationが停止する。元characterは破棄・復活させない。source actorとshapeは既存Registry／transactionの所有権へ従う。

そのOnDisableや既存公開通知中のTryCutはUnavailable。Disposeは要求だけを記録し、外側finallyで分類holdを返してから未移譲資源を解放する。移譲済みshape／actorやtransactionの分類は解放しない。World.Shutdownの再入もその外側まで保留し、通常のShutdownへつなぐ。終了要求後に一度Publishedと返る場合でも、外側scopeがそのWorldの通常終了を既に開始していることがある。待機や強制Job Completeは行わない。

これは任意callback安全性ではない。通知中の直接Driver.EndCut／Registry改変／World Destroy／rig改変などを許可した検証ではない。prepared TryCut／handle Dispose／World.Shutdownの3入口の再入を対象とする。

## 部分失敗

- D1 falseはterminal、未移譲資源を解放する。原因別復旧はしない。
- append後のD3拒否／upload例外ではStorageが残る。まだRegistryへ移っていなければ発行fragmentをRetireし、shape・専用actorを解放する。旧characterは維持。Storage巻戻し・display修復とは呼ばない。
- Registry呼出しの例外でも実際にownerが挿入済みかを確認し、そのshapeを二重解放しない。Registry／DAGへ部分登録済みならWorld所有を維持し、handleはterminal。正常継続や自動retryを許可する結果ではなく、callerが既存World終了等の上位異常処理を行う。今回一般rollback／例外全てのPlayer終了配線は追加しない。
- Driverへの移譲後例外ではtransactionが分類を保持し、既存EndCut／World終了に任せる。公開済みpairをhandle Disposeで撤去しない。

## 検証方法と限界

新規EditModeは合成tetra render＋box B-rep。通常公開、一回移譲、EmptySide／Full各3拒否→別pose、入力拒否、D1 false、D3早期false／upload例外、Registry前／DAG途中例外、公開後例外、Busy・保留Dispose、World終了再入、別handle再入を対象とする。failure注入はtest側のreflection・既存公開hookのみで、新しい汎用runtime fault hookは作らない。

新規PlayModeは実OnDisableで同frame退出・再入拒否・Dispose保留を確認し、一方は通常Unity更新でFinalまで、もう一方は退出通知中のWorld.Shutdownを検証する。さらに辺テーブル未設定の負例では同frame公開後のKernelFailed→Abort／回収・旧character非復活を確認する。既存fixtureのendingは最大120通常frameでdrainを確認し、未回収のまま強制破棄しない。実Inertial入力、再切断、実Job回収保留の新製品基点検証は後続private版で行う。

### PlayMode初回失敗の分類

`play-1`は1/2通過。同期公開は成立したがFinal確認がHandedOffではなくRecoveredとなった。`play-2`も1/2で、期待条件を変えず失敗メッセージだけを追加し`CutOutcome=KernelFailed`を確認した。再利用した既存mass activation用`NewAuthoredShape`はface loopを持つ一方、edge／faceEdge配列をゼロ初期化したままで、数値kernel用の検証済みauthoringではなかった。

正常系の新fixtureではface loopから12辺と両隣接faceを構築する。製品runtimeやkernel、Finalの期待値は変更せず、`play-3`は3/3通過した（正常Final、通知中Shutdown、元の不正辺テーブルを使う負例）。最初の2失敗runとソースsnapshotは保持する。入力の不備をhot全走査の追加やkernelの許容拡大で回避していない。この修正はテスト用authoringの修正であり、製品一般入力検証の拡張ではない。

新しいprivate候補`Working/D6T-cdb53ea/Project`を使い、製品本体と旧候補は起動しない。既存Licensed／adopted fixtureはprivate内だけにコピーし、commitしない。rawは製品`Logs/CharacterRequestMigration`。`Run.ps1`は変更8 C#をrunごとに保存し、全Runtime／Testsを製品候補と比較する。`Inspect.py --check`で現在候補・fixture・XML／log・summaryを照合する。旧cold／leaseのsource固定とverifierは新HEADへ書き換えない。

## 実行結果（Unity 6000.3.22f1）

| 最終ソースのrun | 結果 | PID |
| --- | --- | --- |
| focused-2 新規EditMode | 16/16 | 44380 |
| full-1 全EditMode | 3,871/3,871 | 21252 |
| play-3 新規PlayMode | 3/3 | 121432 |
| related-1 D5・mass activationを含むfixture | 10/10 | 96484 |

上記はfail／skip／inconclusive 0。新規EditMode16件は全体3,871件に、新規PlayMode3件は関連10件にも含まれるため加算しない。全Runtime／Tests 1,417ファイルとfixture 4群が元とbyte一致。製品Unity生成設定に変更なし。性能・native memory peakは未測定。

以前の`focused-1`は16/16（PID 63440）。その後のPlayMode fixture更新前のsource snapshotなので、最終ソースの再照合はfocused-2を使う。`play-1`（PID 76408）と診断メッセージ追加のみの`play-2`（PID 67680）は各1/2の失敗として残す。7 run間で製品runtime 4ファイルのSHAは同じで、修正はPlayMode fixtureだけ。保存summaryは過去runの最終ソースとの不一致も明記し、失敗や古い成功を新ソースの成功へ読み替えない。

次工程は、この製品commitを基点とする新private環境で実Inertial／Casual／Professionalを接続し、同frame公開・Final／再切断・実Job未回収中の終了を再確認する。通常Gameplayの自動配線／DESIGN移行完了宣言、Player／IL2CPP、同境界の性能比較はまだ行わない。
