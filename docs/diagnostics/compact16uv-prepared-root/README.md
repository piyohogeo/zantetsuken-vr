# Prepared Direct16 root display component (D3)

2026-09-25。製品基点`44dfccc7e72bb2bcdd185c1c1b6bbfc5d2f088b7`。B／V12のproducer別cold表示準備を限定移植した。privateのglobal arm、診断時計、任意objectを「検証済み」とみなす入口は取り込まず、呼出しごとに枠と生成済み出力を渡す。通常Gameplay入口は未切替。

## 呼出しと費用

1. coldで`display.TryPrepareRoot(directSkinInput, out slot)`。単一submeshのmaterial ID 0を解決し、Shown、instance Listの初期容量、空reflected、command／material／range配列を準備する。displayのShown Listにも既存数＋未使用枠数分の容量を準備する。
2. pose確定後に`directSkinInput.TryAppendForDisplay(storage, out output)`。既存D1の同期skin／bounds／index補正コピーと同じ処理で、追加の出力receiptは値型。生成元producer・Storage・Geometryを保持し、任意callerが別Geometryを詰めたreceiptを生成するpublic APIはない。従来`TryAppendTo`はそのまま残す。
3. `display.TryShowPreparedRoot(slot, output, fragment, objectToWorld, lineageToGeometryLocal)`。対応するproducer／display／Storage、公開済みindex世代、記録済みextentを確認し、現在のindex開始位置でcold commandを埋める。その後は共通のTryShow処理で転送・参照登録・採用する。index／submesh全走査、draw command用index lease取得、material辞書lookup、Shown／配列生成をこのprepared pathでは行わない。

published extent・行列／section契約・live fragment／lineage・frame確定・容量・参照表の検査、実GPU転送、geometry／display instance参照取得は残る。「TryShow全体がallocation zero」「転送が不要」「高速化率を達成した」とはしない。

枠はStorage／GPU／参照slot／論理ID／admission budgetを予約しない。cold準備後に一般登録がShown Listの余白を使い切った場合、prepared登録はhotでListを増やさず早期falseになる。D4で必要な同時対象数の容量を別途準備する。coldで解決したmaterial bindingは固定で、bindingを差し替えるなら未使用枠を破棄・再作成する。material自体の破棄はhotでも拒否する。

## 所有権と失敗

- 枠は一つのdisplayと一つのD1 producerに結び付く。別producer／World／Storageの出力、default出力、退役済み世代は転送前に拒否する。古いreceiptから新世代のindex領域を読めない。
- 早期拒否は枠を消費しない。後続frame／別の明示要求へ再利用できるが、自動retryはしない。
- 転送開始直前に枠をconsumeし、displayの未使用枠listからO(1)で外す。GPU転送のfalse／例外、参照表不足でも消費済みを戻さない。global arm／共有scratchが次の一般登録へ残る経路はない。
- 参照登録前のfalseではGeometryはcaller所有のPublishedのまま。slot破棄はappendや転送のrollbackではなく、必要ならcallerが既存Storage契約でindexを退役させる。VB tailは戻さない。GPU更新例外後は既存どおりdisplayがbrokenとなり再利用できない。
- 未使用slotのDisposeはcold entry／配列への参照を外し、display Disposeも全未使用slotを解放する。Shown Listの確保済み容量はslot Disposeでは縮小しない。成功後のslot Disposeは採用済みShownを破棄しない。以後はdisplayが既存の参照寿命を所有する。
- 出力receipt自体はGeometry／Storageの所有権やleaseを持たない。producerをDisposeしても、既に出力済みGeometryと準備済み枠は有効。ただしDispose済みproducerから新規cold枠は作らない。

## 検証

新規合成EditMode **19/19通過**（focused-1、skip 0）。cold枠分離・ID／Storage／参照の非予約、非zero VB／IB開始位置で一般commandとの一致、cold配列の同一性、2 producer独立、異なるproducer／Storage／display・default・退役世代拒否、早期frame／placement／fragment拒否、容量・参照表不足、GPU転送例外、未使用枠／display解放、materialのcold固定・破棄、producer Dispose後の出力利用、再登録拒否、frame callback中のslot Disposeを確認した。

GPU例外testはtest側だけのreflectionでbufferを破棄し、実upload入口を通す。新しい製品用failure hookや計時・診断counterは追加しない。一般登録・Final／recutは既存TryShow／TryCommitCutを維持し、prepared slotを暗黙に使用しない。

初回全EditMode `full-1`は3,775/3,776。既存`VpAsyncStorageCutTests.TwoKernelsOfOneStorage_RunOnWorkersAtTheSameTime`だけが、両処理終了後の「2 kernelの実行時間が重なる」assertで失敗した。テスト・worker実装は変更しておらず、同テストはD3 APIを使わない。原因は断定しない。ソース・閾値を変更しない単独追試`worker-recheck-1`は1/1通過、2 threadの実行重なり0.325 msを記録した。この数値は試験の成立証拠であり性能測定ではない。初回失敗を削除・成功扱いにせず、全体追試も別runへ保存する。

無変更の全体追試`full-2`は**3,776/3,776通過**（既存3,757＋新規19、skip／inconclusive 0）。最初のworker時間gate未達を解決済みとはせず、1回目の失敗と再現しなかった追試を両方残す。全4 runとsource／XML／log SHAは[summary.json](summary.json)。raw証拠は`Logs/PreparedRootMigration/`に保持する。Unity起動によるOpenXR設定の自動変更は起動前のclean内容へ戻した。

これはcomponent移植であり、private実アセットでD1＋D2＋D3を通した証拠、Player／IL2CPP／性能、D4–D6・Gameplay採用の完了ではない。

次はprivateの実asset検証へこの明示slot経路を接続し、D1＋D2＋D3の登録／Final／再切断を確認する。旧44dfcccの検証snapshotは保持する。その後D4／D5の容量・空anchor・初回準備を接続し、D6の同frame hit境界と拒否／Abort／staleを検証する。
