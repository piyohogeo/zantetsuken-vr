# Per-instance prepared physics component migration (D2)

2026-09-25。製品基点 `1ebe2289fd12ac2c4f6ec1f3b4714e0cf26c1cff`。B／V12から**個体別bone-local Meshのcold cookと現在poseのB-rep出力**を限定移植した。ライセンス素材、private診断コード、staticなMesh→可変Frame辞書、保持inactive pairは持ち込んでいない。通常Gameplayの初回登録入口は未切替。

## 費用の位置と対応範囲

- `VpPreparedPhysicsInput(bank, ranges)`はcold準備。authoringで検証済みのbone-local凸B-repから、個体専用Meshを各1個生成し、`PhysicsCutCook.DefaultCooking`で各1回cookする。入力topology・bind位置はコピーし、数値出力bank／Frame配列もここで確保する。source Collider／Rigidbodyは生成しない。
- `TryPose(boneToOwner, out borrowedShape)`はMainの一回用処理。全convexの現在剛体行列を確認後、B-rep位置へ直接変換出力し、そのループでconvex／全体boundsを集計する。Meshの変更・再cook・Actor生成・登録・公開は行わない。pose行列取得は呼出側の責任で、全convexは同一poseに対応させる。
- Frameはshapeのconvexごとの不変値。Provisional view／Final継承／再切断viewが引き継ぐ。骨local Meshは共通ShapeFrame下の専用子に配置し、切断で新規生成されたMeshは従来どおり数値座標系に直接配置する。FinalはMesh・cook設定・全体frameに加えてconvex frameが同じ場合だけ既存Colliderを再利用する。
- Final交換・準備取消では、builder自身のリストが所有する専用子も解放する。GameObject名を所有権の判定に使わない。frameなしの従来経路は同じShapeFrame上のColliderを維持する。

入力B-repのpointer容量・閉じた凸topologyは既存authoring契約を前提とし、このクラスを任意の未検証bufferの安全検査器にしない。coldではfinite位置・face loopの範囲／参照を確認する。hotの行列rigid判定と出力finite判定は現在poseに依存するため残す。scale／reflection／shearは非対応。固定scaleは前段で焼き込み、bone-to-ownerには剛体変換を渡す。

要求ごとに必要なB-rep変換とbounds、分類、mass、実Collider／Actor構築、公開費用は残る。`ColdCookCalls`はこの入力constructorが明示的に呼んだcook数であり、Unity内部の再cookを捕捉するProfilerではない。hotから明示cookがなくなったことと、連続Mainの性能測定は区別する。

## 所有権

`TryPose`の出力は借用。`TakeShape()`が入力から呼出側への一回だけの所有権移譲であり、Registry受理を意味しない。登録に失敗した呼出側は受け取ったshapeをDisposeする。移譲前なら成功pose後でも入力Disposeがshapeを解放する。cold構築途中に失敗したMeshも回収する。

shapeのwork hold、子孫のbank hold／Mesh source holdを既存契約で維持する。親終了で使用中の子孫Meshを破棄しない。最後の参照が終了してからMeshを解放する。失敗poseも一回用入力を消費するため、その後の別pose要求にはcold準備を作り直す。これは受付前のretry方針を実装したものではなく、D6で消費境界を接続する必要がある。

## 検証と限界

合成boxだけを用いた新規20/20テストが通過（`focused-1`、skip 0）。複数個体のMesh分離、B-rep／Frame／Collider配置とbounds、元bind位置保持、scale等の拒否、cold途中失敗、未使用／未移譲／移譲後破棄、work／子孫保持、Provisional配置、Finalの再利用／交換／取消、専用子の回収、直接Final builderを確認した。

Finalの継承・生成切替テストのproductsは制御された合成レコードであり、新規形状の数値cut／非同期cookを実行した証拠ではない。Editor内のinactive candidate／component検証で、物理sceneへの公開、query／solver、PlayMode遅延Destroy、実assetの19骨proxy、元Bakeとの代表pose比較、Player／IL2CPP／速度を検証したことにはしない。

全EditModeも**3,757/3,757通過**（既存3,737＋新規20、`full-1`、失敗／skip／inconclusive 0）。両runともUnity exit 0だけでなくXMLの実行件数を確認した。source／証拠SHAは[summary.json](summary.json)に記録。raw証拠は製品worktreeの`Logs/PreparedPhysicsMigration/`。Unity起動だけで変化したOpenXR設定は起動前のclean内容へ戻し、製品差分には含めない。

次はprivate実assetで製品D1＋D2の接続・Final／recut／終了を確認し、その後D3–D5のcold登録準備、D6の同frame hit／公開境界へ進む。通常Gameplayの切替、外部hit所有権、拒否／Abort／stale／worker保留の統合完了は宣言しない。従来B／V12の性能結果も製品達成値へ転記しない。
