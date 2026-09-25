# 空anchor／cold容量準備（D4）

2026-09-25。製品基点`0d5313044ec1844e81a9cd5caf3d77201a546058`。B／V12から空anchorの構造的省略と明示capacityだけを限定移植した。privateのON/OFF flag、計時counter、fresh一体に固定したhelperは取り込まない。新規APIへの通常Gameplay接続・D5／D6・製品性能達成は別工程。

## 空anchor

登録のnull／空入力は従来からnull表現だった。`CopyAnchors`はその判定を残し、nonemptyのコピー／finite検査／List生成を別のNoInlining helperへ分離した。空入力に実コピーがあったと主張する変更ではなく、割当てを含むhelperへ入らない構造にする。

`PrepareAnchorDistribution`はcanonical nullなら配分用Listを2個作らず、共通のplane／epsilon検証からcount 0の結果を得る。結果の準備済みflag・epsilon・Revision・Publish要件は維持する。準備済み再呼出しは従来どおり結果を返し、改めて与えたepsilonで変更しない。nonempty経路はコピー・分類・on-plane両側配分を維持し、その結果で空になった側もnullへ揃えるため、当該子の再切断も空専用経路を使える。

public `FixedSupportAnchors.TryDistribute`の出力null／alias拒否と検証順は維持する。空だから不正plane／epsilonを受け入れたり、未準備を準備済みとみなしたりしない。Abortは元sourceを退役し、staleは別扱いのまま。

## cold容量API

全てMainのcold準備用、**必要な総容量（high-water）**を渡す。追加の予約数ではなく、同値・小さい値で反復しても縮小・加算しない。無効な負数は変更前に拒否する。Registryのpair数×2はcheckedで検査してから確保する。

| API | 対象／必要数の意味 |
| --- | --- |
| `LogicalCutLedger.PrepareCapacity(fragments, operations)` | lifetime発行済み履歴を含む2つのList。二体＋各1回の論理両側公開ならfragment 6・operation 2。retired履歴は消えないので同時live数だけでは不足し得る |
| `PhysicsOwnerRegistry.PrepareCapacity(owners, pairs)` | owner／bodyの2辞書とpair operation／source／bodyの3辞書。二体同時Provisionalならpair 2、pair body最低4。Final前後に保持するowner数は別途見積もる |
| `CutDag.PrepareGeometryCapacity(fragments)` | geometry／frame辞書のみ。rootに加えて保持中の子・履歴を含める。node／fault／withoutGeometryやWork／Storageは対象外 |
| `VpLogicalCutDisplay.PrepareShownCapacity(shown)` | Shown Listだけ。既存Shown＋未使用D3枠を下回らない。GPU・参照slot・候補／branch buffer容量や新規Shown entryは対象外 |

ID、Revision、admission budget、Storage／GPU／reference slot、Actor／Collider、Workは作成・予約しない。メモリ確保失敗まで含む複数collectionの原子的rollbackは保証しない。足りない総容量を指定した後の一般経路は従来の動的grow、D3 prepared表示は既存の不足拒否契約に従う。後の一般登録が余白を使えるため「二体を予約した」という意味ではない。各APIは同じWorldで使う各実体にcoldで呼ぶ。通常Gameplayからの容量API呼出しはまだ追加していない。

## 検証

`focused-1`で新規20/20、二体同時Provisional公開テストを追加した`focused-2`で21/21、skip 0。初回成功時の変更ソースもrawディレクトリの`source/`へ保持した。

全EditMode `full-1`は**3,797/3,797通過**（既存3,776＋D4新規21、skip／inconclusive 0）。今回の3 runに失敗はない。前D3で一度失敗したworker時間重なりテストも今回通過したが、過去の失敗原因を解明・修復したという意味ではない。Unityが自動変更したOpenXR設定は、実行前のcleanな内容へ戻した。

- null／空配列／空List、配分ListがnullのままPublish→再切断、準備前Publish拒否、準備済みepsilon不変。
- 負／NaN／±Infinity epsilonの無変更拒否、zero／NaN／Infinity planeと有限の最小・最大normalで一般経路との一致。
- Abort／stale、二重結果、nonempty入力の独立コピーと片側空結果のcanonical化。
- Ledgerの総容量反復、二体・2公開の無grow、ID／Revision／budgetの非予約、容量を確保してもbudget不足は拒否。
- Registry各5辞書の必要容量・非登録、二体の実Provisional公開（pair 2・body 4）で無grow・source／side写像維持。
- DAG二root登録とdisplay二producer表示で準備容量を維持、DAG終了／display Dispose後の準備拒否。

テストのreflectionは容量と内部null表現を読むためだけに使用し、製品用診断counterを増やしていない。List 2個を作らない構造と容量無growを検査したのであって、GCゼロ・初回metadata費用ゼロ・要求全体のµs短縮を測ったものではない。

## 再現・未完了

Unity 6000.3.22f1、非表示batchmodeのEditMode。専用filterは`D4_`、全体はfilterなし。raw XML／logは`Logs/ColdCapacityMigration/`。別Unityの性能用占有制御は行わず、実行時間を性能値に使わない。

`C:/Python38/python.exe docs/diagnostics/compact16uv-cold-capacity/Inspect.py --check`で[summary.json](summary.json)のXML／logと変更対象source SHAを照合する。旧D1＋D2＋D3 private snapshot・旧V19・過去数値を変更していない。素材の追加・pushなし。

次はD5（fresh joint既定setter省略・初回準備）を限定移植し、D6の実hit公開境界へ進む。実assetでD4を含めた再統合、同frame PlayerLoop・拒否／Abort／stale／寿命の全契約、Player／IL2CPP・連続Main・cold費用・画像は未完了。
